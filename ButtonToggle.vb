Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Linq
Imports System.Windows.Forms
Imports Grasshopper
Imports Grasshopper.Kernel
Imports Grasshopper.Kernel.Data
Imports Grasshopper.GUI.Canvas
Imports Grasshopper.Kernel.Types
Imports Rhino.Display
Imports Rhino.Geometry

''' <summary>One viewport button/toggle slot: world point or plane, plus optional clickable geometry and label text.</summary>
Friend Structure ButtonToggleSlot
    Public Location As Point3d
    Public Plane As Plane
    Public HasPlane As Boolean
    Public ClickArea As GeometryBase
    Public HasClickArea As Boolean
    Public Label As String
End Structure

''' <summary>Fingerprint used for proximity matching and save-shifted toggle restoration.</summary>
Friend Structure ButtonToggleProximityKey
    Public Location As Point3d
    Public HasPlane As Boolean
    Public PlaneZx As Double
    Public PlaneZy As Double
    Public PlaneZz As Double
End Structure

''' <summary>Toggle state saved when its point leaves the input list (Save shifted).</summary>
Friend Structure ShiftedButtonEntry
    Public Key As ButtonToggleProximityKey
    Public Value As Boolean
End Structure

''' <summary>
''' Viewport button / toggle: click a point, optional surface, or optional text label.
''' Mode 0 = Toggle (sticky boolean); Mode 1 = Button (True while pressed).
''' </summary>
Public Class ButtonToggleComp
    Inherits GH_Component
    Implements IGH_VariableParameterComponent

    Private Enum ZuiOptionalKind
        None = -1
        Mode = 0
        ClickableArea = 1
        Text = 2
        Size = 3
        TextDefault = 4
        TextHover = 5
        TextClicked = 6
        EdgeDefault = 7
        EdgeHover = 8
        EdgeClicked = 9
        FillDefault = 10
        FillHover = 11
        FillClicked = 12
        Font = 13
        Active = 14
        LockUnselected = 15
        PreserveChanges = 16
        ProximityCache = 17
        SaveShifted = 18
        ClearCache = 19
        WorkWhenHidden = 20
    End Enum

    Private Shared ReadOnly ZuiCanonicalOrder As ZuiOptionalKind() = {
        ZuiOptionalKind.Mode,
        ZuiOptionalKind.ClickableArea,
        ZuiOptionalKind.Text,
        ZuiOptionalKind.Size,
        ZuiOptionalKind.TextDefault,
        ZuiOptionalKind.TextHover,
        ZuiOptionalKind.TextClicked,
        ZuiOptionalKind.EdgeDefault,
        ZuiOptionalKind.EdgeHover,
        ZuiOptionalKind.EdgeClicked,
        ZuiOptionalKind.FillDefault,
        ZuiOptionalKind.FillHover,
        ZuiOptionalKind.FillClicked,
        ZuiOptionalKind.Font,
        ZuiOptionalKind.Active,
        ZuiOptionalKind.LockUnselected,
        ZuiOptionalKind.WorkWhenHidden,
        ZuiOptionalKind.PreserveChanges,
        ZuiOptionalKind.ProximityCache,
        ZuiOptionalKind.ClearCache
    }

    Private Const BaseInputCount As Integer = 1
    Friend Const ModeToggle As Integer = 0
    Friend Const ModeButton As Integer = 1

    Friend Structure ButtonToggleSlotSettings
        Public Active As Boolean
        Public Mode As Integer
        Public TextHeight As Double
        Public FontFace As String
        Public TextDefault As Color
        Public HasTextDefault As Boolean
        Public TextHover As Color
        Public HasTextHover As Boolean
        Public TextClicked As Color
        Public HasTextClicked As Boolean
        Public EdgeDefault As Color
        Public HasEdgeDefault As Boolean
        Public EdgeHover As Color
        Public HasEdgeHover As Boolean
        Public EdgeClicked As Color
        Public HasEdgeClicked As Boolean
        Public FillDefault As Color
        Public HasFillDefault As Boolean
        Public FillHover As Color
        Public HasFillHover As Boolean
        Public FillClicked As Color
        Public HasFillClicked As Boolean
        Public Label As String
        Public ClickArea As GeometryBase
        Public HasClickArea As Boolean
    End Structure

    Friend SlotSettings As ButtonToggleSlotSettings()

    Public Sub New()
        MyBase.New("Button / Toggle", "Button",
                   "Viewport button or toggle: click a point, plane-aligned text, optional surface (Ca), or text label (Tx). Mode 0 = toggle, 1 = button.",
                   "Params", "Util")
        TagMouse = New ButtonToggleMouse(Me)
    End Sub

#Region "Component overrides"

    Private Shared _icon As Bitmap

    Private Shared Function BuildIcon24x24() As Bitmap
        Const w As Integer = 24, h As Integer = 24
        Dim bmp As New Bitmap(w, h, PixelFormat.Format32bppArgb)
        Using g As Graphics = Graphics.FromImage(bmp)
            g.Clear(Color.Transparent)
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            Using br As New SolidBrush(Color.FromArgb(255, 230, 110, 55))
                g.FillRectangle(br, 3, 5, 18, 14)
            End Using
            Using pn As New Pen(Color.FromArgb(255, 40, 40, 40), 1)
                g.DrawRectangle(pn, 3, 5, 18, 14)
            End Using
            Using br As New SolidBrush(Color.FromArgb(255, 255, 255, 255))
                g.FillEllipse(br, 9, 9, 6, 6)
            End Using
            Using pn As New Pen(Color.FromArgb(255, 40, 40, 40), 1)
                g.DrawEllipse(pn, 9, 9, 6, 6)
            End Using
        End Using
        Return bmp
    End Function

    Protected Overrides ReadOnly Property Icon() As Bitmap
        Get
            If _icon Is Nothing Then _icon = BuildIcon24x24()
            Return _icon
        End Get
    End Property

    Public Overrides ReadOnly Property ComponentGuid() As Guid
        Get
            Return New Guid("{a4e2c8f1-9b3d-4e70-8c5a-1f6d0e9b2a47}")
        End Get
    End Property

    Protected Overrides Sub RegisterInputParams(pManager As GH_Component.GH_InputParamManager)
        Dim p As New Grasshopper.Kernel.Parameters.Param_Geometry With {
            .Name = "Location",
            .NickName = "P",
            .Description = "Optional point (text faces camera) or plane (text drawn in plane) per button/toggle (tree). Omit when clickable area (Ca) alone defines the controls.",
            .Access = GH_ParamAccess.tree,
            .Optional = True
        }
        pManager.AddParameter(p)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddBooleanParameter("State", "B", "Boolean state per point (toggle sticky; button True while pressed).", GH_ParamAccess.tree)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New ButtonToggleCompAtt(Me)
    End Sub

    Public Overrides ReadOnly Property IsPreviewCapable As Boolean
        Get
            Return True
        End Get
    End Property

    Public Overrides Sub AddedToDocument(document As GH_Document)
        MyBase.AddedToDocument(document)
        ViewportPreview.EnsureGrasshopperDocumentHooks(document)
        SyncOptionalInputsFromFlags()
        SyncMouse()
    End Sub

    Public Overrides Sub RemovedFromDocument(document As GH_Document)
        ShutDownInteraction()
        MyBase.RemovedFromDocument(document)
    End Sub

    Public Overrides Sub DocumentContextChanged(document As GH_Document, context As GH_DocumentContext)
        MyBase.DocumentContextChanged(document, context)
        If context = GH_DocumentContext.Close Then ShutDownInteraction()
    End Sub

    Private Sub ShutDownInteraction()
        If TagMouse IsNot Nothing Then
            TagMouse.Enabled = False
            TagMouse.SetHoverPollActive(False)
        End If
    End Sub

    Protected Overrides Sub AfterSolveInstance()
        MyBase.AfterSolveInstance()
        SyncMouse()
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Protected Overrides Sub AppendAdditionalComponentMenuItems(ByVal menu As Windows.Forms.ToolStripDropDown)
        Dim modeVal As Integer = EffectiveModeForMenu()
        Dim toggleItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Toggle", AddressOf Menu_ModeToggle, True, modeVal = ModeToggle)
        toggleItem.ToolTipText = "Sticky boolean: each click flips On/Off."

        Dim buttonItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Button", AddressOf Menu_ModeButton, True, modeVal = ModeButton)
        buttonItem.ToolTipText = "Momentary boolean: True while the mouse is held down on the control."

        Menu_AppendSeparator(menu)

        Dim lockUnsel As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Lock unselected", AddressOf Menu_LockUnselected, True, MenuBoolChecked(LockUnselected, ZuiOptionalKind.LockUnselected))
        lockUnsel.ToolTipText = "When on, controls can be clicked only while this component is selected on the Grasshopper canvas."

        Dim workHidden As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Work when hidden", AddressOf Menu_WorkWhenHidden, True, MenuBoolChecked(WorkWhenHidden, ZuiOptionalKind.WorkWhenHidden))
        workHidden.ToolTipText = "When on, viewport clicking still works while this component is Hidden (preview off)."

        Dim listCache As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "List cache", AddressOf Menu_PreserveChanges, True, MenuBoolChecked(PreserveChanges, ZuiOptionalKind.PreserveChanges))
        listCache.ToolTipText = "Keep toggle state by tree path / list index when locations move. With Proximity also on: keep by index for far moves; proximity remaps wrap-shifts, culls, grafts, and tree changes."

        Dim proximity As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, MenuBoolChecked(ProximityCache, ZuiOptionalKind.ProximityCache))
        proximity.ToolTipText = "Re-attach state by nearest cached location on wrap-shifts, culls, grafts, and other list/tree changes. Culled anchors are always saved and restored if they return (save-shifted)."

        Dim cc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Reset all toggle/button states."
    End Sub

    Private Sub Menu_ModeToggle()
        RecordUndoEvent("Button Mode", New ButtonToggleUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.Mode, False)
        ControlMode = ModeToggle
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ModeButton()
        RecordUndoEvent("Button Mode", New ButtonToggleUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.Mode, False)
        ControlMode = ModeButton
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_LockUnselected()
        RecordUndoEvent("Button Lock Unselected", New ButtonToggleUndo(Me))
        LockUnselected = Not LockUnselected
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_WorkWhenHidden()
        RecordUndoEvent("Button Work When Hidden", New ButtonToggleUndo(Me))
        WorkWhenHidden = Not WorkWhenHidden
        SyncMouse()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Button List Cache", New ButtonToggleUndo(Me))
        PreserveChanges = Not PreserveChanges
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Button Proximity", New ButtonToggleUndo(Me))
        ProximityCache = Not ProximityCache
        If ProximityCache Then SaveShifted = True
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ClearCache()
        RecordUndoEvent("Button Clear Cache", New ButtonToggleUndo(Me))
        ClearStateCacheInternal()
    End Sub

#End Region

#Region "Optional inputs / ZUI"

    Private Function FindInputIndexByNick(nick As String) As Integer
        For i As Integer = 0 To Params.Input.Count - 1
            If String.Equals(Params.Input(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Shared Function NickNameForZuiKind(kind As ZuiOptionalKind) As String
        Select Case kind
            Case ZuiOptionalKind.Mode : Return "Md"
            Case ZuiOptionalKind.ClickableArea : Return "Ca"
            Case ZuiOptionalKind.Text : Return "Tx"
            Case ZuiOptionalKind.Size : Return "S"
            Case ZuiOptionalKind.TextDefault : Return "Td"
            Case ZuiOptionalKind.TextHover : Return "Th"
            Case ZuiOptionalKind.TextClicked : Return "Tc"
            Case ZuiOptionalKind.EdgeDefault : Return "Ed"
            Case ZuiOptionalKind.EdgeHover : Return "Eh"
            Case ZuiOptionalKind.EdgeClicked : Return "Ec"
            Case ZuiOptionalKind.FillDefault : Return "Fd"
            Case ZuiOptionalKind.FillHover : Return "Fh"
            Case ZuiOptionalKind.FillClicked : Return "Fc"
            Case ZuiOptionalKind.Font : Return "Fn"
            Case ZuiOptionalKind.Active : Return "Ac"
            Case ZuiOptionalKind.LockUnselected : Return "Lu"
            Case ZuiOptionalKind.WorkWhenHidden : Return "Wh"
            Case ZuiOptionalKind.PreserveChanges : Return "Pr"
            Case ZuiOptionalKind.ProximityCache : Return "Px"
            Case ZuiOptionalKind.SaveShifted : Return "Ss"
            Case ZuiOptionalKind.ClearCache : Return "Cc"
            Case Else : Return String.Empty
        End Select
    End Function

    Private Function HasZuiInput(kind As ZuiOptionalKind) As Boolean
        If kind = ZuiOptionalKind.None Then Return False
        Return FindInputIndexByNick(NickNameForZuiKind(kind)) >= 0
    End Function

    Private Function NextZuiKindToInsert() As ZuiOptionalKind
        For Each kind As ZuiOptionalKind In ZuiCanonicalOrder
            If Not HasZuiInput(kind) Then Return kind
        Next
        Return ZuiOptionalKind.None
    End Function

    Private Function CanonicalInsertIndex(kind As ZuiOptionalKind) As Integer
        Dim idx As Integer = BaseInputCount
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If k = kind Then Return idx
            If HasZuiInput(k) Then idx += 1
        Next
        Return Math.Max(BaseInputCount, Params.Input.Count)
    End Function

    Private Shared Function CreateBoolZuiParam(name As String, nick As String, description As String,
                                               Optional access As GH_ParamAccess = GH_ParamAccess.item) As Grasshopper.Kernel.Parameters.Param_Boolean
        Return New Grasshopper.Kernel.Parameters.Param_Boolean With {
            .Optional = True,
            .Name = name,
            .NickName = nick,
            .Description = description,
            .Access = access
        }
    End Function

    Private Shared Function CreateColourZuiParam(name As String, nick As String, description As String) As Grasshopper.Kernel.Parameters.Param_Colour
        Return New Grasshopper.Kernel.Parameters.Param_Colour With {
            .Optional = True,
            .Name = name,
            .NickName = nick,
            .Description = description,
            .Access = GH_ParamAccess.tree
        }
    End Function

    Private Function CreateZuiParam(kind As ZuiOptionalKind) As IGH_Param
        Select Case kind
            Case ZuiOptionalKind.Mode
                Return New Grasshopper.Kernel.Parameters.Param_Integer With {
                    .Optional = True,
                    .Name = "Mode",
                    .NickName = "Md",
                    .Description = "Per-item control mode (tree paths match P): 0 = Toggle, 1 = Button.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.ClickableArea
                Return New Grasshopper.Kernel.Parameters.Param_Geometry With {
                    .Optional = True,
                    .Name = "Clickable area",
                    .NickName = "Ca",
                    .Description = "Surface/brep/mesh hit target (tree). Paths match P when Location is wired; when P is empty, Ca alone defines the button/toggle slots.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Text
                Return New Grasshopper.Kernel.Parameters.Param_String With {
                    .Optional = True,
                    .Name = "Text",
                    .NickName = "Tx",
                    .Description = "Optional clickable text label per point (tree paths match P).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Size
                Return New Grasshopper.Kernel.Parameters.Param_Number With {
                    .Optional = True,
                    .Name = "Size",
                    .NickName = "S",
                    .Description = "Text height per label in model units (tree paths match P).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.TextDefault
                Return CreateColourZuiParam("Text default", "Td",
                    "Point and text colour when idle (tree paths match P).")
            Case ZuiOptionalKind.TextHover
                Return CreateColourZuiParam("Text hover", "Th",
                    "Point and text colour while hovered (tree paths match P).")
            Case ZuiOptionalKind.TextClicked
                Return CreateColourZuiParam("Text clicked", "Tc",
                    "Point and text colour when toggled on / button pressed (tree paths match P).")
            Case ZuiOptionalKind.EdgeDefault
                Return CreateColourZuiParam("Edge default", "Ed",
                    "Clickable-area edge colour when idle (tree paths match P).")
            Case ZuiOptionalKind.EdgeHover
                Return CreateColourZuiParam("Edge hover", "Eh",
                    "Clickable-area edge colour while hovered (tree paths match P).")
            Case ZuiOptionalKind.EdgeClicked
                Return CreateColourZuiParam("Edge clicked", "Ec",
                    "Clickable-area edge colour when toggled on / button pressed (tree paths match P).")
            Case ZuiOptionalKind.FillDefault
                Return CreateColourZuiParam("Surface default", "Fd",
                    "Clickable-area surface fill colour when idle (tree paths match P). Alpha controls opacity.")
            Case ZuiOptionalKind.FillHover
                Return CreateColourZuiParam("Surface hover", "Fh",
                    "Clickable-area surface fill colour while hovered (tree paths match P). Alpha controls opacity.")
            Case ZuiOptionalKind.FillClicked
                Return CreateColourZuiParam("Surface clicked", "Fc",
                    "Clickable-area surface fill colour when toggled on / button pressed (tree paths match P). Alpha controls opacity.")
            Case ZuiOptionalKind.Font
                Return New Grasshopper.Kernel.Parameters.Param_String With {
                    .Optional = True,
                    .Name = "Font",
                    .NickName = "Fn",
                    .Description = "Font face name per label (tree paths match P).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Active
                Return CreateBoolZuiParam("Active", "Ac",
                    "When true, viewport picking is enabled for that item (overrides Lock unselected). Tree paths match P.",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.LockUnselected
                Return CreateBoolZuiParam("Lock unselected", "Lu",
                    "When true, viewport picking works only while this component is selected on the Grasshopper canvas.")
            Case ZuiOptionalKind.WorkWhenHidden
                Return CreateBoolZuiParam("Work when hidden", "Wh",
                    "When true, viewport clicking still works while this component is Hidden (preview off).")
            Case ZuiOptionalKind.PreserveChanges
                Return CreateBoolZuiParam("List cache", "Pr",
                    "Keep toggle state by tree path / list index when locations move. With Proximity: keep by index for far moves; proximity remaps wrap-shifts and tree changes.")
            Case ZuiOptionalKind.ProximityCache
                Return CreateBoolZuiParam("Proximity cache", "Px",
                    "Re-attach state by nearest cached location on wrap-shifts, culls, grafts, and other list/tree changes. Culled anchors are always saved and restored if they return.")
            Case ZuiOptionalKind.SaveShifted
                Return CreateBoolZuiParam("Save shifted", "Ss",
                    "Legacy input; ignored. Save-shifted is always active when Proximity cache is on.")
            Case ZuiOptionalKind.ClearCache
                Return CreateBoolZuiParam("Clear cache", "Cc",
                    "Pulse true to reset all toggle/button states (rising edge only).")
        End Select
        Return Nothing
    End Function

    Private Sub SetZuiKindEnabled(kind As ZuiOptionalKind, enabled As Boolean)
        If kind = ZuiOptionalKind.None Then Return
        If enabled Then
            If HasZuiInput(kind) Then Return
            Dim param As IGH_Param = CreateZuiParam(kind)
            If param Is Nothing Then Return
            Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
        Else
            Dim ix As Integer = FindInputIndexByNick(NickNameForZuiKind(kind))
            If ix < 0 Then Return
            Dim p As IGH_Param = Params.Input(ix)
            p.RemoveAllSources()
            Params.UnregisterInputParameter(p)
        End If
        SyncFeatureFlagsFromInputs()
        Params.OnParametersChanged()
    End Sub

    Friend Sub SyncOptionalInputsFromFlags()
        VariableParameterMaintenance()
        Params.OnParametersChanged()
    End Sub

    Private Sub SyncFeatureFlagsFromInputs()
        ' Mode remains a stored ControlMode unless Md is wired; other flags stay on the component.
    End Sub

    Private Function ZuiInputWired(ix As Integer) As Boolean
        If ix < 0 OrElse Params Is Nothing Then Return False
        Dim p As IGH_Param = Params.Input(ix)
        Return p IsNot Nothing AndAlso p.SourceCount > 0
    End Function

    Private Function ReadBoolInputVolatile(ix As Integer, defaultIfUnwired As Boolean) As Boolean
        If ix < 0 OrElse Params Is Nothing Then Return defaultIfUnwired
        Dim p As IGH_Param = Params.Input(ix)
        If p Is Nothing OrElse p.SourceCount = 0 Then Return defaultIfUnwired
        If p.VolatileDataCount = 0 Then Return defaultIfUnwired
        Dim goo As IGH_Goo = p.VolatileData.AllData(True).FirstOrDefault()
        Dim gb As GH_Boolean = TryCast(goo, GH_Boolean)
        If gb IsNot Nothing Then Return gb.Value
        Return defaultIfUnwired
    End Function

    Private Function ReadIntInputVolatile(ix As Integer, defaultIfUnwired As Integer) As Integer
        If ix < 0 OrElse Params Is Nothing Then Return defaultIfUnwired
        Dim p As IGH_Param = Params.Input(ix)
        If p Is Nothing OrElse p.SourceCount = 0 Then Return defaultIfUnwired
        If p.VolatileDataCount = 0 Then Return defaultIfUnwired
        Dim goo As IGH_Goo = p.VolatileData.AllData(True).FirstOrDefault()
        Dim gi As GH_Integer = TryCast(goo, GH_Integer)
        If gi IsNot Nothing Then Return gi.Value
        Return defaultIfUnwired
    End Function

    Private Function MenuBoolChecked(defaultValue As Boolean, kind As ZuiOptionalKind) As Boolean
        Dim ix As Integer = FindInputIndexByNick(NickNameForZuiKind(kind))
        If ix >= 0 AndAlso ZuiInputWired(ix) Then Return ReadBoolInputVolatile(ix, defaultValue)
        Return defaultValue
    End Function

    Private Function EffectiveModeForMenu() As Integer
        Dim mdIx As Integer = FindInputIndexByNick("Md")
        If mdIx >= 0 AndAlso ZuiInputWired(mdIx) Then
            Return ClampMode(ReadIntInputVolatile(mdIx, ControlMode))
        End If
        Return ClampMode(ControlMode)
    End Function

    Private Shared Function ClampMode(mode As Integer) As Integer
        If mode = ModeButton Then Return ModeButton
        Return ModeToggle
    End Function

    Private Sub ApplyBoolInput(DA As IGH_DataAccess, ix As Integer, ByRef target As Boolean, defaultIfUnwired As Boolean)
        If ix < 0 Then Return
        If Params.Input(ix).SourceCount = 0 Then Return
        Dim v As Boolean = defaultIfUnwired
        If DA.GetData(ix, v) Then target = v
    End Sub

    Private Sub ApplyZuiBooleanInputs(DA As IGH_DataAccess)
        ApplyBoolInput(DA, FindInputIndexByNick("Lu"), LockUnselected, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Wh"), WorkWhenHidden, False)
        ApplyBoolInput(DA, FindInputIndexByNick("Pr"), PreserveChanges, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Px"), ProximityCache, False)
        If ProximityCache Then SaveShifted = True Else SaveShifted = False

        Dim ccIx As Integer = FindInputIndexByNick("Cc")
        If ccIx >= 0 AndAlso Params.Input(ccIx).SourceCount > 0 Then
            Dim pulse As Boolean = False
            If DA.GetData(ccIx, pulse) Then
                If pulse AndAlso Not _clearCacheInputPrev Then ClearStateCacheInternal()
                _clearCacheInputPrev = pulse
            End If
        Else
            _clearCacheInputPrev = False
        End If
    End Sub

    Private Sub ClearStateCacheInternal()
        States.Clear()
        PressedSlots.Clear()
        ShiftedEntries.Clear()
        CacheSlots = Nothing
        CacheSlotPaths = Nothing
        CacheSlotBranchIndices = Nothing
        CacheTreeKeys = Nothing
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

    Private Function IsActiveForViewport() As Boolean
        Dim acIx As Integer = FindInputIndexByNick("Ac")
        If acIx < 0 Then Return True
        If HasZuiInput(ZuiOptionalKind.Active) Then
            If SlotSettings Is Nothing Then Return True
            For i As Integer = 0 To SlotSettings.Length - 1
                If SlotSettings(i).Active Then Return True
            Next
            Return False
        End If
        Return ReadBoolInputVolatile(acIx, True)
    End Function

    Friend Function IsSlotActiveForViewport(index As Integer) As Boolean
        If Not IsSelectionAllowedForViewport() Then Return False
        If index < 0 Then Return False
        If HasZuiInput(ZuiOptionalKind.Active) AndAlso SlotSettings IsNot Nothing AndAlso index < SlotSettings.Length Then
            Return SlotSettings(index).Active
        End If
        Return IsActiveForViewport()
    End Function

    Private Function IsSelectionAllowedForViewport() As Boolean
        If HasZuiInput(ZuiOptionalKind.Active) Then
            If Not IsActiveForViewport() Then Return False
        ElseIf Not IsActiveForViewport() Then
            Return False
        End If
        If FindInputIndexByNick("Ac") >= 0 Then Return True
        Dim luIx As Integer = FindInputIndexByNick("Lu")
        If luIx >= 0 Then
            Return Not ReadBoolInputVolatile(luIx, True) OrElse (Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected)
        End If
        Return Not LockUnselected OrElse (Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected)
    End Function

    Private Function KindToInsertAt(index As Integer) As ZuiOptionalKind
        If index < BaseInputCount OrElse index > Params.Input.Count Then Return ZuiOptionalKind.None
        If index = Params.Input.Count Then Return NextZuiKindToInsert()
        Dim targetSlot As Integer = index - BaseInputCount
        Dim canonSlot As Integer = 0
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If canonSlot = targetSlot Then
                If Not HasZuiInput(k) Then Return k
                Return ZuiOptionalKind.None
            End If
            canonSlot += 1
        Next
        Return ZuiOptionalKind.None
    End Function

    Private Function IsRemovableZuiInput(index As Integer) As Boolean
        If index < BaseInputCount OrElse index >= Params.Input.Count Then Return False
        Dim nick As String = Params.Input(index).NickName
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If String.Equals(nick, NickNameForZuiKind(k), StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

#End Region

#Region "Variable parameters"

    Public Function CanInsertParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanInsertParameter
        If side <> GH_ParameterSide.Input Then Return False
        Return KindToInsertAt(index) <> ZuiOptionalKind.None
    End Function

    Public Function CanRemoveParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanRemoveParameter
        If side <> GH_ParameterSide.Input Then Return False
        Return IsRemovableZuiInput(index)
    End Function

    Public Function CreateParameter(side As GH_ParameterSide, index As Integer) As IGH_Param Implements IGH_VariableParameterComponent.CreateParameter
        If side <> GH_ParameterSide.Input Then Return Nothing
        Dim kind As ZuiOptionalKind = KindToInsertAt(index)
        If kind = ZuiOptionalKind.None Then Return Nothing
        Return CreateZuiParam(kind)
    End Function

    Public Function DestroyParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.DestroyParameter
        Return True
    End Function

    Public Sub VariableParameterMaintenance() Implements IGH_VariableParameterComponent.VariableParameterMaintenance
        SyncFeatureFlagsFromInputs()
    End Sub

#End Region

#Region "State"

    Friend States As New List(Of Boolean)
    Friend PressedSlots As New List(Of Boolean)
    Friend SlotPaths As New List(Of GH_Path)
    Friend SlotBranchIndices As New List(Of Integer)

    ''' <summary>List cache: keep states by tree path / list index when locations move. With ProximityCache: mixed mode.</summary>
    Public PreserveChanges As Boolean = True
    ''' <summary>Proximity cache: remap by nearest location when list/tree structure changes. Save-shifted always on with this flag.</summary>
    Public ProximityCache As Boolean = False
    ''' <summary>Always mirrors ProximityCache. Kept for serialization / undo compatibility.</summary>
    Public SaveShifted As Boolean = False
    Public LockUnselected As Boolean = True
    ''' <summary>When true, viewport clicking stays enabled while the component is Hidden (preview off).</summary>
    Public WorkWhenHidden As Boolean = False
    Public ControlMode As Integer = ModeToggle

    Friend Slots As New List(Of ButtonToggleSlot)
    Private CacheSlots As List(Of ButtonToggleSlot) = Nothing
    Private CacheSlotPaths As List(Of GH_Path) = Nothing
    Private CacheSlotBranchIndices As List(Of Integer) = Nothing
    Private CacheTreeKeys As List(Of String) = Nothing
    Friend ShiftedEntries As New List(Of ShiftedButtonEntry)

    Friend TextHeight As Double = 1.0R
    Friend FontFace As String = String.Empty
    Friend TagColour As Color = Color.Black

    Friend TagMouse As ButtonToggleMouse
    Friend HoverIndex As Integer = -1
    Private Const HoverTextScale As Double = 1.15R
    Friend Const TagPickRadiusPx As Double = 14.0R
    Private _clearCacheInputPrev As Boolean = False

    Friend Function TextHeightForIndex(index As Integer) As Double
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim h As Double = SlotSettings(index).TextHeight
            If h > 0 AndAlso Not Double.IsNaN(h) Then Return h
        End If
        Return TextHeight
    End Function

    Friend Function FontFaceForIndex(index As Integer) As String
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim f As String = SlotSettings(index).FontFace
            If Not String.IsNullOrWhiteSpace(f) Then Return f.Trim()
        End If
        Return If(FontFace, String.Empty).Trim()
    End Function

    Friend Function TagColourForIndex(index As Integer) As Color
        Return TextColourForIndex(index, False, False)
    End Function

    Friend Function TextColourForIndex(index As Integer, isHover As Boolean, isOn As Boolean) As Color
        Dim baseCol As Color = TagColour
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim s As ButtonToggleSlotSettings = SlotSettings(index)
            If s.HasTextDefault Then baseCol = s.TextDefault
            If isHover AndAlso s.HasTextHover Then Return s.TextHover
            If isOn AndAlso s.HasTextClicked Then Return s.TextClicked
            If (Not isHover) AndAlso (Not isOn) AndAlso s.HasTextDefault Then Return s.TextDefault
            If isHover AndAlso s.HasTextDefault Then
                Return Color.FromArgb(s.TextDefault.A,
                                      Math.Min(255, s.TextDefault.R + 30),
                                      Math.Min(255, s.TextDefault.G + 30),
                                      Math.Min(255, s.TextDefault.B + 30))
            End If
            If isOn AndAlso s.HasTextDefault Then
                Return Color.FromArgb(s.TextDefault.A,
                                      Math.Min(255, s.TextDefault.R + 40),
                                      Math.Min(255, CInt(s.TextDefault.G * 0.55R) + 120),
                                      Math.Min(255, CInt(s.TextDefault.B * 0.4R)))
            End If
        End If
        If isHover Then
            Return Color.FromArgb(baseCol.A, Math.Min(255, baseCol.R + 30), Math.Min(255, baseCol.G + 30), Math.Min(255, baseCol.B + 30))
        End If
        If isOn Then
            Return Color.FromArgb(baseCol.A, Math.Min(255, baseCol.R + 40), Math.Min(255, CInt(baseCol.G * 0.55R) + 120), Math.Min(255, CInt(baseCol.B * 0.4R)))
        End If
        Return baseCol
    End Function

    Friend Function EdgeColourForIndex(index As Integer, isHover As Boolean, isOn As Boolean) As Color
        Dim baseCol As Color = TextColourForIndex(index, False, False)
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim s As ButtonToggleSlotSettings = SlotSettings(index)
            If isHover AndAlso s.HasEdgeHover Then Return s.EdgeHover
            If isOn AndAlso s.HasEdgeClicked Then Return s.EdgeClicked
            If (Not isHover) AndAlso (Not isOn) AndAlso s.HasEdgeDefault Then Return s.EdgeDefault
            ' Fallbacks when only some overrides exist.
            If isHover AndAlso s.HasEdgeDefault Then
                Return Color.FromArgb(Math.Min(255, s.EdgeDefault.A + 40),
                                      Math.Min(255, s.EdgeDefault.R + 30),
                                      Math.Min(255, s.EdgeDefault.G + 30),
                                      Math.Min(255, s.EdgeDefault.B + 30))
            End If
            If isOn AndAlso s.HasEdgeDefault Then
                Return Color.FromArgb(s.EdgeDefault.A,
                                      Math.Min(255, s.EdgeDefault.R + 20),
                                      Math.Min(255, CInt(s.EdgeDefault.G * 0.55R) + 100),
                                      Math.Min(255, CInt(s.EdgeDefault.B * 0.4R)))
            End If
        End If
        If isHover Then
            Return Color.FromArgb(230, Math.Min(255, baseCol.R + 30), Math.Min(255, baseCol.G + 30), Math.Min(255, baseCol.B + 30))
        End If
        If isOn Then
            Return Color.FromArgb(200, Math.Min(255, baseCol.R + 40), Math.Min(255, CInt(baseCol.G * 0.55R) + 120), Math.Min(255, CInt(baseCol.B * 0.4R)))
        End If
        Return Color.FromArgb(150, baseCol)
    End Function

    Friend Function FillColourForIndex(index As Integer, isHover As Boolean, isOn As Boolean) As Color
        Dim baseCol As Color = TextColourForIndex(index, False, False)
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim s As ButtonToggleSlotSettings = SlotSettings(index)
            If isHover AndAlso s.HasFillHover Then Return EnsureFillAlpha(s.FillHover, 120)
            If isOn AndAlso s.HasFillClicked Then Return EnsureFillAlpha(s.FillClicked, 70)
            If (Not isHover) AndAlso (Not isOn) AndAlso s.HasFillDefault Then Return EnsureFillAlpha(s.FillDefault, 35)
            If isHover AndAlso s.HasFillDefault Then
                Dim d As Color = EnsureFillAlpha(s.FillDefault, 35)
                Return Color.FromArgb(120, Math.Min(255, d.R + 50), Math.Min(255, d.G + 50), Math.Min(255, d.B + 50))
            End If
            If isOn AndAlso s.HasFillDefault Then
                Dim d As Color = EnsureFillAlpha(s.FillDefault, 35)
                Return Color.FromArgb(70, Math.Min(255, d.R + 20), Math.Min(255, CInt(d.G * 0.55R) + 90), Math.Min(255, CInt(d.B * 0.4R)))
            End If
        End If
        If isHover Then
            Return Color.FromArgb(120, Math.Min(255, baseCol.R + 50), Math.Min(255, baseCol.G + 50), Math.Min(255, baseCol.B + 50))
        End If
        If isOn Then
            Return Color.FromArgb(70, Math.Min(255, baseCol.R + 20), Math.Min(255, CInt(baseCol.G * 0.55R) + 90), Math.Min(255, CInt(baseCol.B * 0.4R)))
        End If
        Return Color.FromArgb(35, baseCol)
    End Function

    Private Shared Function EnsureFillAlpha(col As Color, defaultAlpha As Integer) As Color
        If col.A = 255 Then Return Color.FromArgb(defaultAlpha, col.R, col.G, col.B)
        Return col
    End Function

    Friend Function ModeForIndex(index As Integer) As Integer
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return ClampMode(SlotSettings(index).Mode)
        End If
        Return ClampMode(ControlMode)
    End Function

    Friend Function LabelForIndex(index As Integer) As String
        If index >= 0 AndAlso index < Slots.Count Then
            Dim lbl As String = Slots(index).Label
            If Not String.IsNullOrEmpty(lbl) Then Return lbl
        End If
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return If(SlotSettings(index).Label, String.Empty)
        End If
        Return String.Empty
    End Function

    Friend Function EffectiveState(index As Integer) As Boolean
        If index < 0 Then Return False
        Dim mode As Integer = ModeForIndex(index)
        If mode = ModeButton Then
            Return index < PressedSlots.Count AndAlso PressedSlots(index)
        End If
        Return index < States.Count AndAlso States(index)
    End Function

    Friend Sub SetHoverIndex(index As Integer)
        If HoverIndex = index Then Return
        HoverIndex = index
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Friend Sub SetStateFromUndo(newStates As List(Of Boolean), newPressed As List(Of Boolean), newPreserve As Boolean, newProximity As Boolean,
                                newSaveShifted As Boolean, newShifted As List(Of ShiftedButtonEntry), newMode As Integer, newLock As Boolean,
                                Optional newWorkWhenHidden As Boolean = False)
        States = New List(Of Boolean)(newStates)
        PressedSlots = New List(Of Boolean)(newPressed)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        SaveShifted = newProximity
        ShiftedEntries = If(newShifted Is Nothing, New List(Of ShiftedButtonEntry), New List(Of ShiftedButtonEntry)(newShifted))
        ControlMode = ClampMode(newMode)
        LockUnselected = newLock
        WorkWhenHidden = newWorkWhenHidden
        Me.ExpireSolution(True)
    End Sub

    Friend Sub SyncMouse()
        If TagMouse Is Nothing Then Return
        Dim previewOk As Boolean = WorkWhenHidden OrElse ViewportPreview.IsEffectivelyPreviewed(Me)
        Dim want As Boolean =
            IsSelectionAllowedForViewport() AndAlso
            previewOk AndAlso
            Slots.Count > 0
        TagMouse.Enabled = want
        TagMouse.SetHoverPollActive(want)
        If Not want Then SetHoverIndex(-1)
    End Sub

    Public Overrides Sub ExpirePreview(redraw As Boolean)
        MyBase.ExpirePreview(redraw)
        SyncMouse()
    End Sub

    Friend Sub ToggleSlot(index As Integer)
        If index < 0 OrElse index >= Slots.Count Then Return
        While States.Count <= index
            States.Add(False)
        End While
        RecordUndoEvent("Button Toggle", New ButtonToggleUndo(Me))
        States(index) = Not States(index)
        Me.ExpireSolution(True)
    End Sub

    Friend Sub SetButtonPressed(index As Integer, isDown As Boolean)
        If index < 0 OrElse index >= Slots.Count Then Return
        While PressedSlots.Count <= index
            PressedSlots.Add(False)
        End While
        If PressedSlots(index) = isDown Then Return
        PressedSlots(index) = isDown
        Me.ExpireSolution(True)
    End Sub

#End Region

#Region "Cache / proximity"

    Private Shared Function TryGetProximityKey(slot As ButtonToggleSlot, ByRef key As ButtonToggleProximityKey) As Boolean
        key = New ButtonToggleProximityKey
        If Not slot.Location.IsValid Then Return False
        key.Location = slot.Location
        key.HasPlane = slot.HasPlane
        If slot.HasPlane Then
            key.PlaneZx = slot.Plane.ZAxis.X
            key.PlaneZy = slot.Plane.ZAxis.Y
            key.PlaneZz = slot.Plane.ZAxis.Z
        End If
        Return True
    End Function

    Private Shared Function MaxProximityMatchDistance() As Double
        Dim tol As Double = 0.01R
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then tol = Math.Max(doc.ModelAbsoluteTolerance * 10.0R, 0.01R)
        Catch
        End Try
        Return tol
    End Function

    Private Shared Function IsWithinProximityMatch(a As ButtonToggleProximityKey, b As ButtonToggleProximityKey) As Boolean
        If a.HasPlane <> b.HasPlane Then Return False
        If Not a.Location.IsValid OrElse Not b.Location.IsValid Then Return False
        Return a.Location.DistanceTo(b.Location) <= MaxProximityMatchDistance()
    End Function

    Private Shared Function ProximityMatchScore(a As ButtonToggleProximityKey, b As ButtonToggleProximityKey) As Double
        If a.HasPlane <> b.HasPlane Then Return Double.PositiveInfinity
        If Not a.Location.IsValid OrElse Not b.Location.IsValid Then Return Double.PositiveInfinity
        Return a.Location.DistanceToSquared(b.Location)
    End Function

    Private Shared Function ShiftedKeyMatchesCandidate(saved As ButtonToggleProximityKey, candidate As ButtonToggleProximityKey) As Boolean
        Return IsWithinProximityMatch(saved, candidate)
    End Function

    Private Shared Function MakeTreeKey(path As GH_Path, branchIndex As Integer) As String
        If path Is Nothing Then Return "#" & branchIndex.ToString()
        Return path.ToString() & "#" & branchIndex.ToString()
    End Function

    Private Shared Function BuildTreeKeys(paths As List(Of GH_Path), branch As List(Of Integer)) As List(Of String)
        If paths Is Nothing OrElse branch Is Nothing Then Return New List(Of String)
        Dim n As Integer = Math.Min(paths.Count, branch.Count)
        Dim keys As New List(Of String)(n)
        For i As Integer = 0 To n - 1
            keys.Add(MakeTreeKey(paths(i), branch(i)))
        Next
        Return keys
    End Function

    Private Shared Function TreeKeysEqual(a As List(Of String), b As List(Of String)) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Count <> b.Count Then Return False
        For i As Integer = 0 To a.Count - 1
            If Not String.Equals(a(i), b(i), StringComparison.Ordinal) Then Return False
        Next
        Return True
    End Function

    Private Shared Function ClonePathList(paths As List(Of GH_Path)) As List(Of GH_Path)
        If paths Is Nothing Then Return New List(Of GH_Path)
        Dim dst As New List(Of GH_Path)(paths.Count)
        For Each p As GH_Path In paths
            dst.Add(If(p Is Nothing, Nothing, New GH_Path(p)))
        Next
        Return dst
    End Function

    Private Sub StoreLocationCache(slots As List(Of ButtonToggleSlot), paths As List(Of GH_Path), branch As List(Of Integer))
        CacheSlots = CloneSlotList(slots)
        CacheSlotPaths = ClonePathList(paths)
        CacheSlotBranchIndices = If(branch Is Nothing, New List(Of Integer), New List(Of Integer)(branch))
        CacheTreeKeys = BuildTreeKeys(CacheSlotPaths, CacheSlotBranchIndices)
    End Sub

    Private Shared Function SlotMetadataEqual(aPaths As List(Of GH_Path), aBranch As List(Of Integer),
                                              bPaths As List(Of GH_Path), bBranch As List(Of Integer)) As Boolean
        Return TreeKeysEqual(BuildTreeKeys(aPaths, aBranch), BuildTreeKeys(bPaths, bBranch))
    End Function

    Private Shared Function SlotLocations(slots As List(Of ButtonToggleSlot)) As List(Of Point3d)
        Dim pts As New List(Of Point3d)
        If slots Is Nothing Then Return pts
        For Each s As ButtonToggleSlot In slots
            pts.Add(If(s.Location.IsValid, s.Location, Point3d.Unset))
        Next
        Return pts
    End Function

    Private Shared Function PreferListKeepByProximityIdentity(oldSlots As List(Of ButtonToggleSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                                              newSlots As List(Of ButtonToggleSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As Boolean
        Dim slotMap As Integer() = ProximityMatching.BuildCenterSlotMap(
            SlotLocations(oldSlots), SlotLocations(newSlots), oldPaths, oldBranch, newPaths, newBranch, requireMatchingPaths:=False)
        Return ProximityMatching.SlotMapIsIndexIdentity(slotMap)
    End Function

    Private Shared Function SlotsChanged(oldSlots As List(Of ButtonToggleSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                         newSlots As List(Of ButtonToggleSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As Boolean
        If oldSlots Is Nothing OrElse newSlots Is Nothing Then Return True
        If oldSlots.Count <> newSlots.Count Then Return True
        Const tol As Double = 0.0001R
        Const ang As Double = 0.002R
        If Not SlotMetadataEqual(oldPaths, oldBranch, newPaths, newBranch) Then Return True
        For i As Integer = 0 To oldSlots.Count - 1
            If oldSlots(i).HasPlane <> newSlots(i).HasPlane Then Return True
            If oldSlots(i).Location.DistanceTo(newSlots(i).Location) > tol Then Return True
            If oldSlots(i).HasPlane Then
                If oldSlots(i).Plane.ZAxis.IsParallelTo(newSlots(i).Plane.ZAxis, ang) <> 1 Then Return True
                If oldSlots(i).Plane.XAxis.IsParallelTo(newSlots(i).Plane.XAxis, ang) <> 1 Then Return True
            End If
        Next
        Return False
    End Function

    Private Shared Function CloneSlotList(src As List(Of ButtonToggleSlot)) As List(Of ButtonToggleSlot)
        If src Is Nothing Then Return New List(Of ButtonToggleSlot)
        Dim dst As New List(Of ButtonToggleSlot)(src.Count)
        For Each s As ButtonToggleSlot In src
            Dim c As ButtonToggleSlot = s
            If s.HasClickArea AndAlso s.ClickArea IsNot Nothing Then
                c.ClickArea = s.ClickArea.Duplicate()
            End If
            dst.Add(c)
        Next
        Return dst
    End Function

    Private Sub AddShiftedEntry(key As ButtonToggleProximityKey, value As Boolean)
        For Each existing As ShiftedButtonEntry In ShiftedEntries
            If ShiftedKeyMatchesCandidate(existing.Key, key) Then Return
        Next
        ShiftedEntries.Add(New ShiftedButtonEntry With {.Key = key, .Value = value})
    End Sub

    Private Sub RememberShiftedStates(oldSlots As List(Of ButtonToggleSlot), prevStates As List(Of Boolean), newSlots As List(Of ButtonToggleSlot))
        If oldSlots Is Nothing OrElse prevStates Is Nothing Then Return
        For oi As Integer = 0 To oldSlots.Count - 1
            Dim ka As ButtonToggleProximityKey = Nothing
            If Not TryGetProximityKey(oldSlots(oi), ka) Then Continue For
            Dim still As Boolean = False
            For Each ns As ButtonToggleSlot In newSlots
                Dim kb As ButtonToggleProximityKey = Nothing
                If TryGetProximityKey(ns, kb) AndAlso ShiftedKeyMatchesCandidate(ka, kb) Then
                    still = True
                    Exit For
                End If
            Next
            If still Then Continue For
            Dim v As Boolean = oi < prevStates.Count AndAlso prevStates(oi)
            AddShiftedEntry(ka, v)
        Next
    End Sub

    Private Sub ApplyShiftedStates(newSlots As List(Of ButtonToggleSlot), ByRef states As List(Of Boolean))
        If newSlots Is Nothing OrElse ShiftedEntries.Count = 0 Then Return
        While states.Count < newSlots.Count
            states.Add(False)
        End While
        Dim used As New HashSet(Of Integer)
        For j As Integer = 0 To newSlots.Count - 1
            Dim kb As ButtonToggleProximityKey = Nothing
            If Not TryGetProximityKey(newSlots(j), kb) Then Continue For
            For si As Integer = 0 To ShiftedEntries.Count - 1
                If used.Contains(si) Then Continue For
                If Not ShiftedKeyMatchesCandidate(ShiftedEntries(si).Key, kb) Then Continue For
                states(j) = ShiftedEntries(si).Value
                used.Add(si)
                Exit For
            Next
        Next
        If used.Count > 0 Then
            Dim remaining As New List(Of ShiftedButtonEntry)
            For si As Integer = 0 To ShiftedEntries.Count - 1
                If Not used.Contains(si) Then remaining.Add(ShiftedEntries(si))
            Next
            ShiftedEntries = remaining
        End If
    End Sub

    Private Sub RemapStatesByProximity(oldSlots As List(Of ButtonToggleSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                       prevStates As List(Of Boolean),
                                       newSlots As List(Of ButtonToggleSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer),
                                       ByRef outStates As List(Of Boolean))
        outStates = New List(Of Boolean)
        For i As Integer = 0 To newSlots.Count - 1
            outStates.Add(False)
        Next
        If oldSlots Is Nothing OrElse prevStates Is Nothing Then Return

        Dim usedOld As New HashSet(Of Integer)
        Dim matchedNew As New HashSet(Of Integer)

        ' Pass 1: same path + branch index when within proximity.
        For ni As Integer = 0 To newSlots.Count - 1
            Dim nKey As ButtonToggleProximityKey = Nothing
            If Not TryGetProximityKey(newSlots(ni), nKey) Then Continue For
            For oi As Integer = 0 To oldSlots.Count - 1
                If usedOld.Contains(oi) Then Continue For
                If oldPaths IsNot Nothing AndAlso newPaths IsNot Nothing AndAlso oi < oldPaths.Count AndAlso ni < newPaths.Count Then
                    If Not oldPaths(oi).Equals(newPaths(ni)) Then Continue For
                End If
                If oldBranch IsNot Nothing AndAlso newBranch IsNot Nothing AndAlso oi < oldBranch.Count AndAlso ni < newBranch.Count Then
                    If oldBranch(oi) <> newBranch(ni) Then Continue For
                End If
                Dim oKey As ButtonToggleProximityKey = Nothing
                If Not TryGetProximityKey(oldSlots(oi), oKey) Then Continue For
                If Not IsWithinProximityMatch(oKey, nKey) Then Continue For
                outStates(ni) = oi < prevStates.Count AndAlso prevStates(oi)
                usedOld.Add(oi)
                matchedNew.Add(ni)
                Exit For
            Next
        Next

        ' Pass 2: greedy nearest remaining.
        For ni As Integer = 0 To newSlots.Count - 1
            If matchedNew.Contains(ni) Then Continue For
            Dim nKey As ButtonToggleProximityKey = Nothing
            If Not TryGetProximityKey(newSlots(ni), nKey) Then Continue For
            Dim bestOi As Integer = -1
            Dim bestScore As Double = Double.PositiveInfinity
            For oi As Integer = 0 To oldSlots.Count - 1
                If usedOld.Contains(oi) Then Continue For
                Dim oKey As ButtonToggleProximityKey = Nothing
                If Not TryGetProximityKey(oldSlots(oi), oKey) Then Continue For
                If Not IsWithinProximityMatch(oKey, nKey) Then Continue For
                Dim score As Double = ProximityMatchScore(oKey, nKey)
                If score < bestScore Then
                    bestScore = score
                    bestOi = oi
                End If
            Next
            If bestOi < 0 Then Continue For
            outStates(ni) = bestOi < prevStates.Count AndAlso prevStates(bestOi)
            usedOld.Add(bestOi)
            matchedNew.Add(ni)
        Next
    End Sub

#End Region

#Region "Tree mapping / solve"

    Private Shared Function TryParseLocationGoo(g As IGH_GeometricGoo, ByRef slot As ButtonToggleSlot) As Boolean
        slot = New ButtonToggleSlot
        If g Is Nothing Then Return False

        Dim ghPl As GH_Plane = TryCast(g, GH_Plane)
        If ghPl IsNot Nothing AndAlso ghPl.IsValid Then
            slot.Plane = ghPl.Value
            slot.Location = ghPl.Value.Origin
            slot.HasPlane = True
            Return True
        End If

        Dim pt As Point3d = Point3d.Unset
        If GH_Convert.ToPoint3d(g, pt, GH_Conversion.Both) AndAlso pt.IsValid Then
            slot.Location = pt
            slot.Plane = Plane.Unset
            slot.HasPlane = False
            Return True
        End If

        Dim pl As New Plane
        If GH_Convert.ToPlane(g, pl, GH_Conversion.Both) AndAlso pl.IsValid Then
            slot.Plane = pl
            slot.Location = pl.Origin
            slot.HasPlane = True
            Return True
        End If

        Return False
    End Function

    Private Shared Function TryGetAnchorFromGeometry(geom As GeometryBase, ByRef pt As Point3d) As Boolean
        pt = Point3d.Unset
        If geom Is Nothing Then Return False
        Try
            Dim bb As BoundingBox = geom.GetBoundingBox(True)
            If Not bb.IsValid Then Return False
            pt = bb.Center
            Return pt.IsValid
        Catch
            Return False
        End Try
    End Function

    Private Shared Function IsClickableAreaGeometry(geom As GeometryBase) As Boolean
        If geom Is Nothing Then Return False
        If TypeOf geom Is Brep OrElse TypeOf geom Is Surface OrElse TypeOf geom Is Extrusion OrElse TypeOf geom Is Mesh Then Return True
        Return False
    End Function

    Private Shared Function TryParseClickAreaGoo(g As IGH_GeometricGoo, ByRef slot As ButtonToggleSlot) As Boolean
        slot = New ButtonToggleSlot
        If g Is Nothing Then Return False
        Dim gb As GeometryBase = GH_Convert.ToGeometryBase(g)
        If gb Is Nothing OrElse Not IsClickableAreaGeometry(gb) Then Return False
        Dim anchor As Point3d = Point3d.Unset
        If Not TryGetAnchorFromGeometry(gb, anchor) Then Return False
        slot.Location = anchor
        slot.Plane = Plane.Unset
        slot.HasPlane = False
        slot.HasClickArea = True
        slot.ClickArea = gb.Duplicate()
        slot.Label = String.Empty
        Return True
    End Function

    ''' <summary>Iterate built slots (from P and/or Ca) so optional inputs map by slot path/index.</summary>
    Private Sub ForEachBuiltSlot(apply As Action(Of Integer, GH_Path, Integer))
        For flat As Integer = 0 To Math.Max(0, Slots.Count) - 1
            Dim path As GH_Path = If(flat < SlotPaths.Count, SlotPaths(flat), New GH_Path(0))
            Dim j As Integer = If(flat < SlotBranchIndices.Count, SlotBranchIndices(flat), flat)
            apply(flat, path, j)
        Next
    End Sub

    Private Sub BuildSlotsFromTree(locData As GH_Structure(Of IGH_GeometricGoo),
                                   slots As List(Of ButtonToggleSlot),
                                   paths As List(Of GH_Path),
                                   branchIndices As List(Of Integer))
        slots.Clear()
        paths.Clear()
        branchIndices.Clear()
        If locData Is Nothing Then Return
        For Each path As GH_Path In locData.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = locData.DataList(path)
            For j As Integer = 0 To branch.Count - 1
                Dim slot As ButtonToggleSlot = Nothing
                If TryParseLocationGoo(branch(j), slot) Then
                    slot.HasClickArea = False
                    slot.ClickArea = Nothing
                    slot.Label = String.Empty
                    slots.Add(slot)
                    paths.Add(path)
                    branchIndices.Add(j)
                End If
            Next
        Next
    End Sub

    ''' <summary>When Location is empty, create one slot per clickable-area geometry.</summary>
    Private Function TryBuildSlotsFromClickArea(DA As IGH_DataAccess,
                                                slots As List(Of ButtonToggleSlot),
                                                paths As List(Of GH_Path),
                                                branchIndices As List(Of Integer)) As Boolean
        Dim ix As Integer = FindInputIndexByNick("Ca")
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return False
        Dim tree As New GH_Structure(Of IGH_GeometricGoo)
        If Not DA.GetDataTree(ix, tree) OrElse tree.DataCount = 0 Then Return False

        slots.Clear()
        paths.Clear()
        branchIndices.Clear()
        For Each path As GH_Path In tree.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = tree.DataList(path)
            For j As Integer = 0 To branch.Count - 1
                Dim slot As ButtonToggleSlot = Nothing
                If TryParseClickAreaGoo(branch(j), slot) Then
                    slots.Add(slot)
                    paths.Add(path)
                    branchIndices.Add(j)
                End If
            Next
        Next
        Return slots.Count > 0
    End Function

    Private Sub MapBoolTreeToSlots(DA As IGH_DataAccess, nick As String,
                                   defaultValue As Boolean, apply As Action(Of Integer, Boolean))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Boolean)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Boolean = defaultValue
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gb As GH_Boolean = tree.AllData(True).FirstOrDefault()
            If gb IsNot Nothing Then broadcast = gb.Value
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim v As Boolean = defaultValue
                If useBroadcast Then
                    v = broadcast
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of GH_Boolean) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        v = valueBranch(j).Value
                    End If
                End If
                apply(flat, v)
            End Sub)
    End Sub

    Private Sub MapIntTreeToSlots(DA As IGH_DataAccess, nick As String, apply As Action(Of Integer, Integer))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Integer)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Integer = ControlMode
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gi As GH_Integer = tree.AllData(True).FirstOrDefault()
            If gi IsNot Nothing Then broadcast = gi.Value
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim v As Integer = ControlMode
                Dim hasV As Boolean = False
                If useBroadcast Then
                    v = broadcast
                    hasV = True
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of GH_Integer) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        v = valueBranch(j).Value
                        hasV = True
                    End If
                End If
                If hasV Then apply(flat, v)
            End Sub)
    End Sub

    Private Sub MapNumberTreeToSlots(DA As IGH_DataAccess, nick As String, apply As Action(Of Integer, Double))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Number)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Double = 0
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gn As GH_Number = tree.AllData(True).FirstOrDefault()
            If gn IsNot Nothing AndAlso gn.IsValid Then broadcast = gn.Value
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim v As Double = 0
                Dim hasV As Boolean = False
                If useBroadcast Then
                    v = broadcast
                    hasV = True
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of GH_Number) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing AndAlso valueBranch(j).IsValid Then
                        v = valueBranch(j).Value
                        hasV = True
                    End If
                End If
                If hasV Then apply(flat, v)
            End Sub)
    End Sub

    Private Sub MapStringTreeToSlots(DA As IGH_DataAccess, nick As String, apply As Action(Of Integer, String))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_String)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As String = String.Empty
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gs As GH_String = tree.AllData(True).FirstOrDefault()
            If gs IsNot Nothing Then broadcast = If(gs.Value, String.Empty)
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim v As String = String.Empty
                Dim hasV As Boolean = False
                If useBroadcast Then
                    v = broadcast
                    hasV = True
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of GH_String) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        v = If(valueBranch(j).Value, String.Empty)
                        hasV = True
                    End If
                End If
                If hasV Then apply(flat, v)
            End Sub)
    End Sub

    Private Sub MapColourTreeToSlots(DA As IGH_DataAccess, nick As String, apply As Action(Of Integer, Color))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Colour)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Color = TagColour
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gc As GH_Colour = tree.AllData(True).FirstOrDefault()
            If gc IsNot Nothing Then broadcast = gc.Value
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim col As Color = TagColour
                Dim hasCol As Boolean = False
                If useBroadcast Then
                    col = broadcast
                    hasCol = True
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of GH_Colour) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        col = valueBranch(j).Value
                        hasCol = True
                    End If
                End If
                If hasCol Then apply(flat, col)
            End Sub)
    End Sub

    Private Sub MapGeometryTreeToSlots(DA As IGH_DataAccess)
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick("Ca")
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of IGH_GeometricGoo)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As IGH_GeometricGoo = Nothing
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            broadcast = tree.AllData(True).FirstOrDefault()
        End If

        ForEachBuiltSlot(
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim gg As IGH_GeometricGoo = Nothing
                If useBroadcast Then
                    gg = broadcast
                ElseIf tree.PathExists(path) Then
                    Dim valueBranch As IList(Of IGH_GeometricGoo) = tree.Branch(path)
                    If valueBranch IsNot Nothing AndAlso j < valueBranch.Count Then gg = valueBranch(j)
                End If
                If gg Is Nothing Then Return
                Dim gb As GeometryBase = GH_Convert.ToGeometryBase(gg)
                If gb Is Nothing OrElse Not IsClickableAreaGeometry(gb) Then Return
                SlotSettings(flat).ClickArea = gb.Duplicate()
                SlotSettings(flat).HasClickArea = True
            End Sub)
    End Sub

    Private Sub BuildSlotSettings(DA As IGH_DataAccess)
        Dim n As Integer = Math.Max(0, Slots.Count)
        If n <= 0 Then
            SlotSettings = Nothing
            Return
        End If

        ReDim SlotSettings(n - 1)
        For i As Integer = 0 To n - 1
            Dim s As ButtonToggleSlotSettings
            s.Active = True
            s.Mode = ClampMode(ControlMode)
            s.TextHeight = TextHeight
            s.FontFace = FontFace
            s.HasTextDefault = False
            s.HasTextHover = False
            s.HasTextClicked = False
            s.HasEdgeDefault = False
            s.HasEdgeHover = False
            s.HasEdgeClicked = False
            s.HasFillDefault = False
            s.HasFillHover = False
            s.HasFillClicked = False
            s.Label = String.Empty
            ' Preserve Ca-only click areas already stored on slots until MapGeometry runs.
            If Slots(i).HasClickArea AndAlso Slots(i).ClickArea IsNot Nothing Then
                s.ClickArea = Slots(i).ClickArea
                s.HasClickArea = True
            Else
                s.ClickArea = Nothing
                s.HasClickArea = False
            End If
            SlotSettings(i) = s
        Next

        If HasZuiInput(ZuiOptionalKind.Active) Then
            MapBoolTreeToSlots(DA, "Ac", True, Sub(i, v) SlotSettings(i).Active = v)
        End If
        If HasZuiInput(ZuiOptionalKind.Mode) Then
            MapIntTreeToSlots(DA, "Md", Sub(i, v) SlotSettings(i).Mode = ClampMode(v))
        End If
        If HasZuiInput(ZuiOptionalKind.Size) Then
            MapNumberTreeToSlots(DA, "S",
                Sub(i, v)
                    If v > 0 AndAlso Not Double.IsNaN(v) Then SlotSettings(i).TextHeight = v
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.TextDefault) Then
            MapColourTreeToSlots(DA, "Td",
                Sub(i, col)
                    SlotSettings(i).TextDefault = col
                    SlotSettings(i).HasTextDefault = True
                End Sub)
        End If
        ' Legacy nick "C" from earlier builds maps to text default.
        If Not HasZuiInput(ZuiOptionalKind.TextDefault) AndAlso FindInputIndexByNick("C") >= 0 Then
            MapColourTreeToSlots(DA, "C",
                Sub(i, col)
                    SlotSettings(i).TextDefault = col
                    SlotSettings(i).HasTextDefault = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.TextHover) Then
            MapColourTreeToSlots(DA, "Th",
                Sub(i, col)
                    SlotSettings(i).TextHover = col
                    SlotSettings(i).HasTextHover = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.TextClicked) Then
            MapColourTreeToSlots(DA, "Tc",
                Sub(i, col)
                    SlotSettings(i).TextClicked = col
                    SlotSettings(i).HasTextClicked = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.EdgeDefault) Then
            MapColourTreeToSlots(DA, "Ed",
                Sub(i, col)
                    SlotSettings(i).EdgeDefault = col
                    SlotSettings(i).HasEdgeDefault = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.EdgeHover) Then
            MapColourTreeToSlots(DA, "Eh",
                Sub(i, col)
                    SlotSettings(i).EdgeHover = col
                    SlotSettings(i).HasEdgeHover = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.EdgeClicked) Then
            MapColourTreeToSlots(DA, "Ec",
                Sub(i, col)
                    SlotSettings(i).EdgeClicked = col
                    SlotSettings(i).HasEdgeClicked = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.FillDefault) Then
            MapColourTreeToSlots(DA, "Fd",
                Sub(i, col)
                    SlotSettings(i).FillDefault = col
                    SlotSettings(i).HasFillDefault = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.FillHover) Then
            MapColourTreeToSlots(DA, "Fh",
                Sub(i, col)
                    SlotSettings(i).FillHover = col
                    SlotSettings(i).HasFillHover = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.FillClicked) Then
            MapColourTreeToSlots(DA, "Fc",
                Sub(i, col)
                    SlotSettings(i).FillClicked = col
                    SlotSettings(i).HasFillClicked = True
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.Font) Then
            MapStringTreeToSlots(DA, "Fn",
                Sub(i, v)
                    If Not String.IsNullOrWhiteSpace(v) Then SlotSettings(i).FontFace = v.Trim()
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.Text) Then
            MapStringTreeToSlots(DA, "Tx", Sub(i, v) SlotSettings(i).Label = If(v, String.Empty))
        End If
        If HasZuiInput(ZuiOptionalKind.ClickableArea) Then
            MapGeometryTreeToSlots(DA)
        End If

        ' Push label / area onto slots for hit-testing & draw.
        For i As Integer = 0 To Slots.Count - 1
            Dim s As ButtonToggleSlot = Slots(i)
            If i < SlotSettings.Length Then
                s.Label = If(SlotSettings(i).Label, String.Empty)
                s.HasClickArea = SlotSettings(i).HasClickArea
                s.ClickArea = SlotSettings(i).ClickArea
            End If
            Slots(i) = s
        Next
    End Sub

    Private Sub SetBooleanOutputTree(DA As IGH_DataAccess)
        Dim outData As New GH_Structure(Of GH_Boolean)
        For i As Integer = 0 To Slots.Count - 1
            Dim path As GH_Path = If(i < SlotPaths.Count, SlotPaths(i), New GH_Path(0))
            outData.Append(New GH_Boolean(EffectiveState(i)), path)
        Next
        DA.SetDataTree(0, outData)
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim locData As New GH_Structure(Of IGH_GeometricGoo)
        DA.GetDataTree(0, locData)

        ApplyZuiBooleanInputs(DA)

        Dim newSlots As New List(Of ButtonToggleSlot)
        Dim newPaths As New List(Of GH_Path)
        Dim newBranchIndices As New List(Of Integer)
        BuildSlotsFromTree(locData, newSlots, newPaths, newBranchIndices)
        If newSlots.Count = 0 Then
            TryBuildSlotsFromClickArea(DA, newSlots, newPaths, newBranchIndices)
        End If

        If newSlots.Count = 0 Then
            SoftClearSlots()
            DA.SetDataTree(0, New GH_Structure(Of GH_Boolean))
            SyncMouse()
            Exit Sub
        End If

        If CacheSlots Is Nothing OrElse CacheTreeKeys Is Nothing Then
            StoreLocationCache(newSlots, newPaths, newBranchIndices)
            If ProximityCache Then
                ApplyShiftedStates(newSlots, States)
            End If
        ElseIf SoftSlotsChanged(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, newSlots, newPaths, newBranchIndices) Then
            Dim newTreeKeys As List(Of String) = BuildTreeKeys(newPaths, newBranchIndices)
            Dim treeChanged As Boolean = Not TreeKeysEqual(CacheTreeKeys, newTreeKeys)
            Dim preferIndexKeep As Boolean = PreserveChanges AndAlso Not treeChanged AndAlso
                (Not ProximityCache OrElse PreferListKeepByProximityIdentity(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, newSlots, newPaths, newBranchIndices))

            If preferIndexKeep Then
                ' List cache: keep states; refresh centroids below.
            ElseIf ProximityCache Then
                Dim prevStates As New List(Of Boolean)(States)
                Dim remapped As List(Of Boolean) = Nothing
                RemapStatesByProximity(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, prevStates,
                                       newSlots, newPaths, newBranchIndices, remapped)
                States = remapped
                RememberShiftedStates(CacheSlots, prevStates, newSlots)
                ApplyShiftedStates(newSlots, States)
            ElseIf Not PreserveChanges Then
                States.Clear()
                PressedSlots.Clear()
            End If
            StoreLocationCache(newSlots, newPaths, newBranchIndices)
        ElseIf ProximityCache Then
            ApplyShiftedStates(newSlots, States)
        End If

        Slots = newSlots
        SlotPaths = newPaths
        SlotBranchIndices = newBranchIndices
        BuildSlotSettings(DA)

        While States.Count < Slots.Count
            States.Add(False)
        End While
        While PressedSlots.Count < Slots.Count
            PressedSlots.Add(False)
        End While
        If Not PreserveChanges AndAlso States.Count > Slots.Count Then
            States.RemoveRange(Slots.Count, States.Count - Slots.Count)
        End If
        If PressedSlots.Count > Slots.Count Then
            PressedSlots.RemoveRange(Slots.Count, PressedSlots.Count - Slots.Count)
        End If

        ' Clear button press for toggle-mode slots.
        For i As Integer = 0 To Slots.Count - 1
            If ModeForIndex(i) = ModeToggle AndAlso i < PressedSlots.Count Then PressedSlots(i) = False
        Next

        Message = If(EffectiveModeForMenu() = ModeButton, "Button", "Toggle")

        SetBooleanOutputTree(DA)
        SyncMouse()
    End Sub

    Private Sub SoftClearSlots()
        Slots.Clear()
        SlotPaths.Clear()
        SlotBranchIndices.Clear()
        CacheSlots = Nothing
        CacheSlotPaths = Nothing
        CacheSlotBranchIndices = Nothing
        CacheTreeKeys = Nothing
    End Sub

    Private Shared Function SoftSlotsChanged(oldSlots As List(Of ButtonToggleSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                             newSlots As List(Of ButtonToggleSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As Boolean
        Return SlotsChanged(oldSlots, oldPaths, oldBranch, newSlots, newPaths, newBranch)
    End Function

#End Region

#Region "Preview / picking"

    Private Shared Sub ApplyFontToText3d(t As Text3d, fontFace As String)
        If t Is Nothing OrElse String.IsNullOrWhiteSpace(fontFace) Then Return
        t.FontFace = fontFace.Trim()
    End Sub

    Friend Function PlaneForSlotViewport(index As Integer, vp As RhinoViewport) As Plane
        Dim s As ButtonToggleSlot = Slots(index)
        If s.HasPlane Then Return s.Plane
        Return New Plane(s.Location, vp.CameraX, vp.CameraY)
    End Function

    Private Shared Function MeasureTextBlockExtents(txt As String, pl As Plane, height As Double, fontFace As String,
                                                    ByRef minX As Double, ByRef maxX As Double,
                                                    ByRef minY As Double, ByRef maxY As Double) As Boolean
        minX = 0 : maxX = 0 : minY = 0 : maxY = 0
        Using t As New Text3d(txt, pl, height)
            ApplyFontToText3d(t, fontFace)
            t.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center
            t.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Middle
            Dim bb As BoundingBox = t.BoundingBox
            If Not bb.IsValid Then Return False
            minX = Double.PositiveInfinity : maxX = Double.NegativeInfinity
            minY = Double.PositiveInfinity : maxY = Double.NegativeInfinity
            For Each c As Point3d In bb.GetCorners()
                Dim lx As Double = (c - pl.Origin) * pl.XAxis
                Dim ly As Double = (c - pl.Origin) * pl.YAxis
                minX = Math.Min(minX, lx) : maxX = Math.Max(maxX, lx)
                minY = Math.Min(minY, ly) : maxY = Math.Max(maxY, ly)
            Next
            Return True
        End Using
    End Function

    Friend Function TryGetSlotScreenRect(vp As RhinoViewport, index As Integer, ByRef screenRect As RectangleF) As Boolean
        screenRect = RectangleF.Empty
        If vp Is Nothing OrElse index < 0 OrElse index >= Slots.Count Then Return False
        Dim s As ButtonToggleSlot = Slots(index)
        If Not s.Location.IsValid Then Return False
        Const padPx As Single = 3.0F
        Dim txt As String = LabelForIndex(index)

        If Not String.IsNullOrEmpty(txt) Then
            Dim pl As Plane = PlaneForSlotViewport(index, vp)
            Dim height As Double = TextHeightForIndex(index)
            Dim fontFace As String = FontFaceForIndex(index)
            Dim minX, maxX, minY, maxY As Double
            If Not MeasureTextBlockExtents(txt, pl, height, fontFace, minX, maxX, minY, maxY) Then Return False
            Dim corners As Point3d() = {
                pl.Origin + pl.XAxis * minX + pl.YAxis * minY,
                pl.Origin + pl.XAxis * maxX + pl.YAxis * minY,
                pl.Origin + pl.XAxis * maxX + pl.YAxis * maxY,
                pl.Origin + pl.XAxis * minX + pl.YAxis * maxY
            }
            Dim minSx As Single = Single.PositiveInfinity, maxSx As Single = Single.NegativeInfinity
            Dim minSy As Single = Single.PositiveInfinity, maxSy As Single = Single.NegativeInfinity
            Dim any As Boolean = False
            For Each wpt As Point3d In corners
                If Not vp.IsVisible(wpt) Then Continue For
                Dim spt As Point2d = vp.WorldToClient(wpt)
                minSx = Math.Min(minSx, CSng(spt.X)) : maxSx = Math.Max(maxSx, CSng(spt.X))
                minSy = Math.Min(minSy, CSng(spt.Y)) : maxSy = Math.Max(maxSy, CSng(spt.Y))
                any = True
            Next
            If Not any Then Return False
            screenRect = New RectangleF(minSx - padPx, minSy - padPx, (maxSx - minSx) + padPx * 2.0F, (maxSy - minSy) + padPx * 2.0F)
            Return True
        End If

        If Not vp.IsVisible(s.Location) Then Return False
        Dim pt As Point2d = vp.WorldToClient(s.Location)
        Dim r As Single = CSng(TagPickRadiusPx)
        screenRect = New RectangleF(CSng(pt.X) - r, CSng(pt.Y) - r, r * 2.0F, r * 2.0F)
        Return True
    End Function

    Private Shared Function TryRayHitGeometry(geom As GeometryBase, ray As Line, ByRef hitPt As Point3d) As Boolean
        hitPt = Point3d.Unset
        If geom Is Nothing OrElse Not ray.IsValid Then Return False
        Dim tol As Double = 0.001R
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then tol = Math.Max(doc.ModelAbsoluteTolerance, 0.001R)
        Catch
        End Try

        Try
            Dim brep As Brep = TryCast(geom, Brep)
            If brep Is Nothing Then
                Dim srf As Surface = TryCast(geom, Surface)
                If srf IsNot Nothing Then brep = srf.ToBrep()
            End If
            If brep Is Nothing Then
                Dim ext As Extrusion = TryCast(geom, Extrusion)
                If ext IsNot Nothing Then brep = ext.ToBrep()
            End If
            If brep IsNot Nothing Then
                Dim overlaps As Curve() = Nothing
                Dim hits As Point3d() = Nothing
                Using lc As New LineCurve(ray)
                    If Rhino.Geometry.Intersect.Intersection.CurveBrep(lc, brep, tol, overlaps, hits) AndAlso hits IsNot Nothing AndAlso hits.Length > 0 Then
                        hitPt = hits(0)
                        Dim bestD2 As Double = hitPt.DistanceToSquared(ray.From)
                        For hi As Integer = 1 To hits.Length - 1
                            Dim d2 As Double = hits(hi).DistanceToSquared(ray.From)
                            If d2 < bestD2 Then
                                bestD2 = d2
                                hitPt = hits(hi)
                            End If
                        Next
                        Return hitPt.IsValid
                    End If
                End Using
            End If

            Dim mesh As Mesh = TryCast(geom, Mesh)
            If mesh IsNot Nothing Then
                Dim hits As Point3d() = Rhino.Geometry.Intersect.Intersection.MeshLine(mesh, ray)
                If hits IsNot Nothing AndAlso hits.Length > 0 Then
                    hitPt = hits(0)
                    Return hitPt.IsValid
                End If
            End If
        Catch
            Return False
        End Try
        Return False
    End Function

    Friend Function PickSlotIndexAtViewport(vp As RhinoViewport, viewportPoint As Drawing.Point) As Integer
        If vp Is Nothing OrElse Slots.Count = 0 Then Return -1
        Dim cursor As New Point2d(CDbl(viewportPoint.X), CDbl(viewportPoint.Y))
        Dim ray As Line = Nothing
        Dim hasRay As Boolean = vp.GetFrustumLine(CDbl(viewportPoint.X), CDbl(viewportPoint.Y), ray)

        Dim bestIx As Integer = -1
        Dim bestMetric As Double = Double.PositiveInfinity

        For i As Integer = 0 To Slots.Count - 1
            If Not IsSlotActiveForViewport(i) Then Continue For
            Dim s As ButtonToggleSlot = Slots(i)

            ' Prefer clickable area ray hits.
            If s.HasClickArea AndAlso s.ClickArea IsNot Nothing AndAlso hasRay Then
                Dim hitPt As Point3d
                If TryRayHitGeometry(s.ClickArea, ray, hitPt) AndAlso vp.IsVisible(hitPt) Then
                    Dim metric As Double = hitPt.DistanceToSquared(ray.From)
                    If metric < bestMetric Then
                        bestMetric = metric
                        bestIx = i
                    End If
                    Continue For
                End If
            End If

            Dim rect As RectangleF
            If Not TryGetSlotScreenRect(vp, i, rect) Then Continue For
            If Not rect.Contains(CSng(cursor.X), CSng(cursor.Y)) Then Continue For
            Dim cx As Double = rect.Left + rect.Width * 0.5R
            Dim cy As Double = rect.Top + rect.Height * 0.5R
            Dim d2 As Double = (cx - cursor.X) * (cx - cursor.X) + (cy - cursor.Y) * (cy - cursor.Y)
            ' Screen hits lose to nearer area hits; among screen hits pick closest center.
            Dim screenMetric As Double = 1.0E+20 + d2
            If screenMetric < bestMetric Then
                bestMetric = screenMetric
                bestIx = i
            End If
        Next

        Return bestIx
    End Function

    Private Shared Sub DrawClickAreaWires(display As DisplayPipeline, geom As GeometryBase, col As Color, thickness As Integer)
        If display Is Nothing OrElse geom Is Nothing Then Return
        Try
            Dim mesh As Mesh = TryCast(geom, Mesh)
            If mesh IsNot Nothing Then
                display.DrawMeshWires(mesh, col, thickness)
                Return
            End If

            Dim ownedBrep As Brep = Nothing
            Dim brep As Brep = TryCast(geom, Brep)
            If brep Is Nothing Then
                Dim srf As Surface = TryCast(geom, Surface)
                If srf IsNot Nothing Then
                    ownedBrep = srf.ToBrep()
                    brep = ownedBrep
                End If
            End If
            If brep Is Nothing Then
                Dim ext As Extrusion = TryCast(geom, Extrusion)
                If ext IsNot Nothing Then
                    ownedBrep = ext.ToBrep()
                    brep = ownedBrep
                End If
            End If
            If brep IsNot Nothing Then
                Try
                    display.DrawBrepWires(brep, col, thickness)
                Finally
                    If ownedBrep IsNot Nothing Then ownedBrep.Dispose()
                End Try
                Return
            End If

            Dim bb As BoundingBox = geom.GetBoundingBox(True)
            If bb.IsValid Then display.DrawBox(bb, col, thickness)
        Catch
        End Try
    End Sub

    Private Shared Sub DrawClickAreaShaded(display As DisplayPipeline, geom As GeometryBase, mat As DisplayMaterial)
        If display Is Nothing OrElse geom Is Nothing OrElse mat Is Nothing Then Return
        Try
            Dim ownedBrep As Brep = Nothing
            Dim brep As Brep = TryCast(geom, Brep)
            If brep Is Nothing Then
                Dim srf As Surface = TryCast(geom, Surface)
                If srf IsNot Nothing Then
                    ownedBrep = srf.ToBrep()
                    brep = ownedBrep
                End If
            End If
            If brep Is Nothing Then
                Dim ext As Extrusion = TryCast(geom, Extrusion)
                If ext IsNot Nothing Then
                    ownedBrep = ext.ToBrep()
                    brep = ownedBrep
                End If
            End If
            If brep IsNot Nothing Then
                Try
                    display.DrawBrepShaded(brep, mat)
                Finally
                    If ownedBrep IsNot Nothing Then ownedBrep.Dispose()
                End Try
                Return
            End If

            Dim mesh As Mesh = TryCast(geom, Mesh)
            If mesh IsNot Nothing Then
                display.DrawMeshShaded(mesh, mat)
                Return
            End If

            Dim subd As SubD = TryCast(geom, SubD)
            If subd IsNot Nothing Then
                Dim sm As Mesh = Mesh.CreateFromSubD(subd, 2)
                If sm IsNot Nothing Then
                    Try
                        display.DrawMeshShaded(sm, mat)
                    Finally
                        sm.Dispose()
                    End Try
                End If
            End If
        Catch
        End Try
    End Sub

    Private Shared Function FillMaterialFromColour(fill As Color) As DisplayMaterial
        Dim alpha As Integer = fill.A
        If alpha <= 0 Then alpha = 1
        Dim mat As New DisplayMaterial(Color.FromArgb(alpha, fill.R, fill.G, fill.B))
        mat.Transparency = 1.0R - (alpha / 255.0R)
        Return mat
    End Function

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        MyBase.DrawViewportWires(args)
        If args Is Nothing OrElse args.Display Is Nothing OrElse Slots.Count = 0 Then Return
        Dim vp As RhinoViewport = Nothing
        Try
            If args.Viewport IsNot Nothing Then vp = args.Viewport
        Catch
        End Try
        If vp Is Nothing Then Return

        For i As Integer = 0 To Slots.Count - 1
            Dim s As ButtonToggleSlot = Slots(i)
            If Not s.Location.IsValid AndAlso Not (s.HasClickArea AndAlso s.ClickArea IsNot Nothing) Then Continue For
            Dim isOn As Boolean = EffectiveState(i)
            Dim isHover As Boolean = (i = HoverIndex)
            Dim col As Color = TextColourForIndex(i, isHover, isOn)

            If s.HasClickArea AndAlso s.ClickArea IsNot Nothing Then
                Dim edgeCol As Color = EdgeColourForIndex(i, isHover, isOn)
                Dim thickness As Integer = If(isHover, 3, If(isOn, 2, 1))
                DrawClickAreaWires(args.Display, s.ClickArea, edgeCol, thickness)
            End If

            Dim txt As String = LabelForIndex(i)
            If Not String.IsNullOrEmpty(txt) AndAlso s.Location.IsValid Then
                Dim pl As Plane = PlaneForSlotViewport(i, vp)
                Dim height As Double = TextHeightForIndex(i) * If(isHover, HoverTextScale, 1.0R)
                Dim fontFace As String = FontFaceForIndex(i)
                Using t3 As New Text3d(txt, pl, height)
                    ApplyFontToText3d(t3, fontFace)
                    t3.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center
                    t3.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Middle
                    args.Display.Draw3dText(t3, col)
                End Using
            ElseIf Not s.HasClickArea AndAlso s.Location.IsValid Then
                Dim style As PointStyle = If(isOn, PointStyle.RoundSimple, PointStyle.RoundControlPoint)
                Dim size As Single = If(isHover, 8.0F, 6.0F)
                If isOn Then size += 1.5F
                args.Display.DrawPoint(s.Location, style, size, col)
            End If
        Next
    End Sub

    Public Overrides Sub DrawViewportMeshes(args As IGH_PreviewArgs)
        MyBase.DrawViewportMeshes(args)
        If args Is Nothing OrElse args.Display Is Nothing OrElse Slots.Count = 0 Then Return

        For i As Integer = 0 To Slots.Count - 1
            Dim s As ButtonToggleSlot = Slots(i)
            If Not s.HasClickArea OrElse s.ClickArea Is Nothing Then Continue For
            Dim isOn As Boolean = EffectiveState(i)
            Dim isHover As Boolean = (i = HoverIndex)
            Dim fill As Color = FillColourForIndex(i, isHover, isOn)
            DrawClickAreaShaded(args.Display, s.ClickArea, FillMaterialFromColour(fill))
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim box As BoundingBox = BoundingBox.Empty
            Dim pad As Double = Math.Max(TextHeight, 1.0R) * 10.0R
            For i As Integer = 0 To Math.Min(Slots.Count, 512) - 1
                Dim s As ButtonToggleSlot = Slots(i)
                If s.Location.IsValid Then box.Union(New BoundingBox(s.Location - New Vector3d(pad, pad, pad), s.Location + New Vector3d(pad, pad, pad)))
                If s.HasClickArea AndAlso s.ClickArea IsNot Nothing Then
                    Try
                        box.Union(s.ClickArea.GetBoundingBox(True))
                    Catch
                    End Try
                End If
            Next
            Return box
        End Get
    End Property

#End Region

#Region "Write/Read"

    Public Overrides Function Write(writer As GH_IO.Serialization.GH_IWriter) As Boolean
        writer.SetBoolean("BT_Preserve", PreserveChanges)
        writer.SetBoolean("BT_Proximity", ProximityCache)
        writer.SetBoolean("BT_SaveShifted", ProximityCache)
        writer.SetBoolean("BT_LockUnselected", LockUnselected)
        writer.SetBoolean("BT_WorkWhenHidden", WorkWhenHidden)
        writer.SetInt32("BT_Mode", ClampMode(ControlMode))
        writer.SetInt32("BT_Count", States.Count)
        For i As Integer = 0 To States.Count - 1
            writer.SetBoolean("BT_State", i, States(i))
        Next
        writer.SetInt32("BT_ShiftedCount", ShiftedEntries.Count)
        For i As Integer = 0 To ShiftedEntries.Count - 1
            Dim entry As ShiftedButtonEntry = ShiftedEntries(i)
            writer.SetDouble("BT_ShiftCx", i, entry.Key.Location.X)
            writer.SetDouble("BT_ShiftCy", i, entry.Key.Location.Y)
            writer.SetDouble("BT_ShiftCz", i, entry.Key.Location.Z)
            writer.SetBoolean("BT_ShiftPlane", i, entry.Key.HasPlane)
            writer.SetDouble("BT_ShiftPz", i, entry.Key.PlaneZx)
            writer.SetDouble("BT_ShiftPy", i, entry.Key.PlaneZy)
            writer.SetDouble("BT_ShiftPzz", i, entry.Key.PlaneZz)
            writer.SetBoolean("BT_ShiftVal", i, entry.Value)
        Next
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(reader As GH_IO.Serialization.GH_IReader) As Boolean
        Dim preserve As Boolean = True
        reader.TryGetBoolean("BT_Preserve", preserve)
        PreserveChanges = preserve

        Dim prox As Boolean = False
        reader.TryGetBoolean("BT_Proximity", prox)
        ProximityCache = prox
        SaveShifted = prox

        Dim loadedSaveShifted As Boolean = False
        reader.TryGetBoolean("BT_SaveShifted", loadedSaveShifted)

        Dim lockUnsel As Boolean = True
        reader.TryGetBoolean("BT_LockUnselected", lockUnsel)
        LockUnselected = lockUnsel

        Dim workHidden As Boolean = False
        reader.TryGetBoolean("BT_WorkWhenHidden", workHidden)
        WorkWhenHidden = workHidden

        Dim mode As Integer = ModeToggle
        If reader.TryGetInt32("BT_Mode", mode) Then ControlMode = ClampMode(mode)

        States.Clear()
        Dim n As Integer = 0
        If reader.TryGetInt32("BT_Count", n) Then
            For i As Integer = 0 To n - 1
                Dim v As Boolean = False
                reader.TryGetBoolean("BT_State", i, v)
                States.Add(v)
            Next
        End If

        ShiftedEntries.Clear()
        Dim shiftedCount As Integer = 0
        If reader.TryGetInt32("BT_ShiftedCount", shiftedCount) AndAlso shiftedCount > 0 Then
            For i As Integer = 0 To shiftedCount - 1
                Dim entry As New ShiftedButtonEntry
                Dim cx As Double = 0, cy As Double = 0, cz As Double = 0
                reader.TryGetDouble("BT_ShiftCx", i, cx)
                reader.TryGetDouble("BT_ShiftCy", i, cy)
                reader.TryGetDouble("BT_ShiftCz", i, cz)
                entry.Key.Location = New Point3d(cx, cy, cz)
                reader.TryGetBoolean("BT_ShiftPlane", i, entry.Key.HasPlane)
                reader.TryGetDouble("BT_ShiftPz", i, entry.Key.PlaneZx)
                reader.TryGetDouble("BT_ShiftPy", i, entry.Key.PlaneZy)
                reader.TryGetDouble("BT_ShiftPzz", i, entry.Key.PlaneZz)
                reader.TryGetBoolean("BT_ShiftVal", i, entry.Value)
                If entry.Key.Location.IsValid Then ShiftedEntries.Add(entry)
            Next
        End If
        SyncOptionalInputsFromFlags()
        Return MyBase.Read(reader)
    End Function

#End Region

End Class

Public Class ButtonToggleCompAtt
    Inherits Grasshopper.Kernel.Attributes.GH_ComponentAttributes

    Public Sub New(owner As ButtonToggleComp)
        MyBase.New(owner)
    End Sub

    Public Overrides Property Selected As Boolean
        Get
            Return MyBase.Selected
        End Get
        Set(value As Boolean)
            MyBase.Selected = value
            Dim comp As ButtonToggleComp = TryCast(Owner, ButtonToggleComp)
            If comp IsNot Nothing Then comp.SyncMouse()
        End Set
    End Property

End Class

Public Class ButtonToggleUndo
    Inherits Grasshopper.Kernel.Undo.GH_UndoAction

    Private ReadOnly _ownerId As Guid
    Private _states As List(Of Boolean)
    Private _pressed As List(Of Boolean)
    Private _preserve As Boolean
    Private _proximity As Boolean
    Private _saveShifted As Boolean
    Private _shifted As List(Of ShiftedButtonEntry)
    Private _mode As Integer
    Private _lockUnselected As Boolean
    Private _workWhenHidden As Boolean

    Sub New(owner As ButtonToggleComp)
        _ownerId = owner.InstanceGuid
        _states = New List(Of Boolean)(owner.States)
        _pressed = New List(Of Boolean)(owner.PressedSlots)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _saveShifted = owner.SaveShifted
        _shifted = If(owner.ShiftedEntries Is Nothing, New List(Of ShiftedButtonEntry), New List(Of ShiftedButtonEntry)(owner.ShiftedEntries))
        _mode = owner.ControlMode
        _lockUnselected = owner.LockUnselected
        _workWhenHidden = owner.WorkWhenHidden
    End Sub

    Protected Overrides Sub Internal_Undo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Protected Overrides Sub Internal_Redo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Private Sub SwapState(doc As GH_Document)
        Dim comp As ButtonToggleComp = TryCast(doc.FindObject(_ownerId, True), ButtonToggleComp)
        If comp Is Nothing Then Return
        Dim curStates As New List(Of Boolean)(comp.States)
        Dim curPressed As New List(Of Boolean)(comp.PressedSlots)
        Dim curPreserve As Boolean = comp.PreserveChanges
        Dim curProximity As Boolean = comp.ProximityCache
        Dim curSaveShifted As Boolean = comp.SaveShifted
        Dim curShifted As List(Of ShiftedButtonEntry) = If(comp.ShiftedEntries Is Nothing, New List(Of ShiftedButtonEntry), New List(Of ShiftedButtonEntry)(comp.ShiftedEntries))
        Dim curMode As Integer = comp.ControlMode
        Dim curLock As Boolean = comp.LockUnselected
        Dim curWorkHidden As Boolean = comp.WorkWhenHidden
        comp.SetStateFromUndo(_states, _pressed, _preserve, _proximity, _saveShifted, _shifted, _mode, _lockUnselected, _workWhenHidden)
        _states = curStates
        _pressed = curPressed
        _preserve = curPreserve
        _proximity = curProximity
        _saveShifted = curSaveShifted
        _shifted = curShifted
        _mode = curMode
        _lockUnselected = curLock
        _workWhenHidden = curWorkHidden
    End Sub

End Class

''' <summary>Viewport clicks on points / text / clickable areas.</summary>
Public Class ButtonToggleMouse
    Inherits Rhino.UI.MouseCallback

    Private ReadOnly Comp As ButtonToggleComp
    Private Const ClickSlopPx As Double = 4.0R

    Private _pendingHit As Integer = -1
    Private _buttonHeld As Integer = -1
    Private _downViewport As Drawing.Point
    Private _hoverTimer As Timer
    Private _hoverPollActive As Boolean
    Private _hookedCanvas As GH_Canvas

    Sub New(owner As ButtonToggleComp)
        Comp = owner
    End Sub

    Friend Sub SetHoverPollActive(active As Boolean)
        _hoverPollActive = active AndAlso Me.Enabled
        If _hoverPollActive Then
            EnsureHoverTimer()
            _hoverTimer.Enabled = True
            AttachGhCanvasHoverHook()
            PollHoverFromGlobalCursor()
        Else
            If _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = False
            DetachGhCanvasHoverHook()
            If Comp IsNot Nothing Then Comp.SetHoverIndex(-1)
        End If
    End Sub

    Private Sub EnsureHoverTimer()
        If _hoverTimer IsNot Nothing Then Return
        _hoverTimer = New Timer With {.Interval = 40}
        AddHandler _hoverTimer.Tick, AddressOf OnHoverPollTick
    End Sub

    Private Sub OnHoverPollTick(sender As Object, e As EventArgs)
        PollHoverFromGlobalCursor()
    End Sub

    Private Sub AttachGhCanvasHoverHook()
        DetachGhCanvasHoverHook()
        Dim cv As GH_Canvas = TryResolveGrasshopperCanvas()
        If cv Is Nothing Then Return
        _hookedCanvas = cv
        AddHandler _hookedCanvas.MouseMove, AddressOf GhCanvas_MouseMoveHover
    End Sub

    Private Sub DetachGhCanvasHoverHook()
        If _hookedCanvas Is Nothing Then Return
        RemoveHandler _hookedCanvas.MouseMove, AddressOf GhCanvas_MouseMoveHover
        _hookedCanvas = Nothing
    End Sub

    Private Sub GhCanvas_MouseMoveHover(sender As Object, e As MouseEventArgs)
        PollHoverFromGlobalCursor()
    End Sub

    Private Shared Function TryResolveGrasshopperCanvas() As GH_Canvas
        Dim cv As GH_Canvas = Grasshopper.Instances.ActiveCanvas
        If cv IsNot Nothing Then Return cv
        Dim ed As Control = TryCast(Grasshopper.Instances.DocumentEditor, Control)
        If ed Is Nothing Then Return Nothing
        Return FindDescendantCanvas(ed)
    End Function

    Private Shared Function FindDescendantCanvas(root As Control) As GH_Canvas
        Dim q As GH_Canvas = TryCast(root, GH_Canvas)
        If q IsNot Nothing Then Return q
        For Each ch As Control In root.Controls
            Dim n As GH_Canvas = FindDescendantCanvas(ch)
            If n IsNot Nothing Then Return n
        Next
        Return Nothing
    End Function

    Friend Sub PollHoverFromGlobalCursor()
        If Not _hoverPollActive OrElse Comp Is Nothing OrElse _pendingHit >= 0 OrElse _buttonHeld >= 0 Then Return
        Try
            Dim screenPt As Drawing.Point = Control.MousePosition
            Dim ghDoc As GH_Document = Comp.OnPingDocument()
            Dim targetDoc As Rhino.RhinoDoc = If(ghDoc IsNot Nothing, ghDoc.RhinoDocument, Nothing)
            If targetDoc Is Nothing Then targetDoc = Rhino.RhinoDoc.ActiveDoc
            If targetDoc Is Nothing Then
                Comp.SetHoverIndex(-1)
                Return
            End If

            For Each view As Rhino.Display.RhinoView In targetDoc.Views
                If view Is Nothing Then Continue For
                If view.Document IsNot targetDoc Then Continue For
                Dim rect As Drawing.Rectangle = view.ScreenRectangle
                If rect.Width <= 0 OrElse rect.Height <= 0 Then Continue For
                If Not rect.Contains(screenPt) Then Continue For
                Dim clientPt As Drawing.Point = view.ScreenToClient(screenPt)
                Dim vp As RhinoViewport = view.ActiveViewport
                If vp Is Nothing Then Continue For
                Comp.SetHoverIndex(Comp.PickSlotIndexAtViewport(vp, clientPt))
                Return
            Next
            Comp.SetHoverIndex(-1)
        Catch
        End Try
    End Sub

    Private Sub UpdateHoverFromViewport(e As Rhino.UI.MouseCallbackEventArgs)
        If Comp Is Nothing OrElse e.View Is Nothing Then Return
        Dim vp As RhinoViewport = e.View.ActiveViewport
        If vp Is Nothing Then Return
        Comp.SetHoverIndex(Comp.PickSlotIndexAtViewport(vp, e.ViewportPoint))
    End Sub

    Private Sub ResumeHoverPoll()
        If _hoverPollActive AndAlso _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = True
        PollHoverFromGlobalCursor()
    End Sub

    ''' <summary>
    ''' After Cancelled viewport clicks, Rhino often ignores the next MouseDown at the same pixel until the cursor moves.
    ''' Flipping Enabled re-arms MouseCallback so rapid clicks on the same clickable area keep working.
    ''' </summary>
    Private Sub RearmMouseCallback()
        Try
            Dim was As Boolean = Me.Enabled
            Me.Enabled = False
            Me.Enabled = was
        Catch
        End Try
    End Sub

    Private Sub FinishClickInteraction(Optional cancelEvent As Rhino.UI.MouseCallbackEventArgs = Nothing)
        ResumeHoverPoll()
        RearmMouseCallback()
        If cancelEvent IsNot Nothing Then cancelEvent.Cancel = True
    End Sub

    Private Sub ReleaseHeldButton()
        If _buttonHeld < 0 OrElse Comp Is Nothing Then
            _buttonHeld = -1
            Return
        End If
        Dim ix As Integer = _buttonHeld
        _buttonHeld = -1
        Comp.SetButtonPressed(ix, False)
    End Sub

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        _pendingHit = -1
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If e.View Is Nothing Then Exit Sub

        ' Clear a stuck momentary press if a prior MouseUp was dropped.
        If _buttonHeld >= 0 Then ReleaseHeldButton()

        Dim vp As RhinoViewport = e.View.ActiveViewport
        If vp Is Nothing Then Exit Sub

        Dim hit As Integer = Comp.PickSlotIndexAtViewport(vp, e.ViewportPoint)
        If hit < 0 Then Exit Sub

        _pendingHit = hit
        _downViewport = e.ViewportPoint
        If _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = False
        Comp.SetHoverIndex(hit)

        If Comp.ModeForIndex(hit) = ButtonToggleComp.ModeButton Then
            _buttonHeld = hit
            Comp.SetButtonPressed(hit, True)
        End If

        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)
        If _pendingHit < 0 AndAlso _buttonHeld < 0 Then
            UpdateHoverFromViewport(e)
            Exit Sub
        End If
        Dim dx As Double = CDbl(e.ViewportPoint.X) - CDbl(_downViewport.X)
        Dim dy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_downViewport.Y)
        If (dx * dx + dy * dy) > (ClickSlopPx * ClickSlopPx) Then
            If _buttonHeld >= 0 Then ReleaseHeldButton()
            _pendingHit = -1
            FinishClickInteraction()
            Exit Sub
        End If
        ' Do not Cancel mouse-move: cancelling moves after a Cancelled mouse-down leaves Rhino
        ' unable to deliver another click until the cursor actually moves.
    End Sub

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseUp(e)
        Dim hit As Integer = _pendingHit
        _pendingHit = -1

        If _buttonHeld >= 0 Then
            ReleaseHeldButton()
            FinishClickInteraction(e)
            Exit Sub
        End If

        If hit < 0 Then
            FinishClickInteraction()
            Exit Sub
        End If
        If Comp Is Nothing Then
            FinishClickInteraction()
            Exit Sub
        End If
        If e.Button <> MouseButtons.Left Then
            FinishClickInteraction()
            Exit Sub
        End If
        If hit >= Comp.Slots.Count Then
            FinishClickInteraction()
            Exit Sub
        End If

        If Comp.ModeForIndex(hit) = ButtonToggleComp.ModeToggle Then
            Comp.ToggleSlot(hit)
        End If

        FinishClickInteraction(e)
    End Sub

End Class
