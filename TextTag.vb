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

''' <summary>One anchor for the text tag component: a world point, optionally with an explicit plane (text orientation).</summary>
Friend Structure TextTagSlot
    Public Location As Point3d
    Public Plane As Plane
    Public HasPlane As Boolean
End Structure

''' <summary>Fingerprint used for proximity matching and save-shifted text restoration.</summary>
Friend Structure TextTagProximityKey
    Public Location As Point3d
    Public HasPlane As Boolean
    Public PlaneZx As Double
    Public PlaneZy As Double
    Public PlaneZz As Double
End Structure

''' <summary>Viewport-entered text saved when its anchor leaves the input list (Save shifted).</summary>
Friend Structure ShiftedTextEntry
    Public Key As TextTagProximityKey
    Public Text As String
    Public UserEdited As Boolean
End Structure

Public Class TextTagComp
    Inherits GH_Component
    Implements IGH_VariableParameterComponent

    Private Enum ZuiOptionalKind
        None = -1
        Size = 0
        Colour = 1
        Font = 2
        Text = 3
        Active = 4
        LockUnselected = 5
        PreserveChanges = 6
        ProximityCache = 7
        SaveShifted = 8
        ClearCache = 9
        JustifyMultiline = 10
        HorizontalAlign = 11
        VerticalAlign = 12
    End Enum

    Private Shared ReadOnly ZuiCanonicalOrder As ZuiOptionalKind() = {
        ZuiOptionalKind.Size,
        ZuiOptionalKind.Colour,
        ZuiOptionalKind.Font,
        ZuiOptionalKind.Text,
        ZuiOptionalKind.Active,
        ZuiOptionalKind.LockUnselected,
        ZuiOptionalKind.PreserveChanges,
        ZuiOptionalKind.ProximityCache,
        ZuiOptionalKind.ClearCache,
        ZuiOptionalKind.JustifyMultiline,
        ZuiOptionalKind.HorizontalAlign,
        ZuiOptionalKind.VerticalAlign
    }

    Private Const BaseInputCount As Integer = 1

    ''' <summary>Per-tag settings resolved from optional tree inputs (paths match location input P).</summary>
    Friend Structure TextTagSlotSettings
        Public Active As Boolean
        Public TextHeight As Double
        Public FontFace As String
        Public TagColour As Color
        Public HasCustomColour As Boolean
        Public HorizontalAlign As Rhino.DocObjects.TextHorizontalAlignment
        Public VerticalAlign As Rhino.DocObjects.TextVerticalAlignment
        Public JustifyMultilineLines As Boolean
    End Structure

    Friend SlotSettings As TextTagSlotSettings()

    Public Sub New()
        MyBase.New("Text Tag", "TextTag",
                   "Viewport text tag: shows a dot at a point (or plane origin); click the dot (component selected) to type text that becomes the output value. Point input = text faces the camera; plane input = text lies in that plane.",
                   "Params", "Util")
        TagMouse = New TextTagMouse(Me)
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
                g.FillEllipse(br, 2, 13, 8, 8)
            End Using
            Using pn As New Pen(Color.FromArgb(255, 40, 40, 40), 1)
                g.DrawEllipse(pn, 2, 13, 8, 8)
            End Using
            Using f As New Font("Arial", 13, FontStyle.Bold, GraphicsUnit.Pixel)
                Using br As New SolidBrush(Color.FromArgb(255, 40, 40, 40))
                    g.DrawString("T", f, br, 10, 1)
                End Using
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
            Return New Guid("{c1f9b1a2-3d64-4b7e-9a5c-2e8f0d6b7a41}")
        End Get
    End Property

    Protected Overrides Sub RegisterInputParams(pManager As GH_Component.GH_InputParamManager)
        pManager.AddGeometryParameter("Location", "P", "Point (text faces camera) or plane (text drawn in plane) to place the tag.", GH_ParamAccess.tree)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddTextParameter("Text", "T", "Entered text per location (empty string until typed).", GH_ParamAccess.tree)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New TextTagCompAtt(Me)
    End Sub

    Public Overrides Sub AddedToDocument(document As GH_Document)
        MyBase.AddedToDocument(document)
        SyncOptionalInputsFromFlags()
    End Sub

    Public Overrides ReadOnly Property IsPreviewCapable As Boolean
        Get
            Return True
        End Get
    End Property

    Public Overrides Sub RemovedFromDocument(document As GH_Document)
        ShutDownInteraction()
        MyBase.RemovedFromDocument(document)
    End Sub

    Public Overrides Sub MovedBetweenDocuments(oldDocument As GH_Document, newDocument As GH_Document)
        ShutDownInteraction()
        MyBase.MovedBetweenDocuments(oldDocument, newDocument)
    End Sub

    Public Overrides Sub DocumentContextChanged(document As GH_Document, context As GH_DocumentContext)
        MyBase.DocumentContextChanged(document, context)
        If context = GH_DocumentContext.Close Then ShutDownInteraction()
    End Sub

    Public Overrides Property Locked As Boolean
        Get
            Return MyBase.Locked
        End Get
        Set(value As Boolean)
            MyBase.Locked = value
            SyncMouse()
        End Set
    End Property

    Protected Overrides Sub AfterSolveInstance()
        MyBase.AfterSolveInstance()
        SyncMouse()
        ' Anchors may have moved even when outputs (texts) are unchanged; make sure viewports repaint the dots/text.
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

#End Region

#Region "Menu"

    Protected Overrides Sub AppendAdditionalComponentMenuItems(menu As Windows.Forms.ToolStripDropDown)

        Dim lockUnsel As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Lock unselected", AddressOf Menu_LockUnselected, True, MenuBoolChecked(LockUnselected, ZuiOptionalKind.LockUnselected))
        lockUnsel.ToolTipText = "When on, tags can be clicked and edited only while this component is selected on the Grasshopper canvas."

        Menu_AppendSeparator(menu)

        Dim listCache As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "List cache", AddressOf Menu_PreserveChanges, True, MenuBoolChecked(PreserveChanges, ZuiOptionalKind.PreserveChanges))
        listCache.ToolTipText = "Keep entered text by tree path / list index when locations move. With Proximity also on: keep by index for far moves; proximity remaps wrap-shifts, culls, grafts, and tree changes."

        Dim proximity As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, MenuBoolChecked(ProximityCache, ZuiOptionalKind.ProximityCache))
        proximity.ToolTipText = "Re-attach text by nearest cached location on wrap-shifts, culls, grafts, and other list/tree changes. Culled anchors are always saved and restored if they return (save-shifted)."

        Dim cc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Erase all entered text."

        Menu_AppendSeparator(menu)

        Dim menuHAlign As Rhino.DocObjects.TextHorizontalAlignment = EffectiveHorizontalAlignForMenu()
        Dim hLeft As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align left", AddressOf Menu_AlignLeft, True, menuHAlign = Rhino.DocObjects.TextHorizontalAlignment.Left)
        hLeft.ToolTipText = "Anchor text to the left of the tag point."

        Dim hMiddle As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align middle", AddressOf Menu_AlignMiddle, True, menuHAlign = Rhino.DocObjects.TextHorizontalAlignment.Center)
        hMiddle.ToolTipText = "Anchor text horizontally centered on the tag point."

        Dim hRight As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align right", AddressOf Menu_AlignRight, True, menuHAlign = Rhino.DocObjects.TextHorizontalAlignment.Right)
        hRight.ToolTipText = "Anchor text to the right of the tag point."

        Menu_AppendSeparator(menu)

        Dim menuVAlign As Rhino.DocObjects.TextVerticalAlignment = EffectiveVerticalAlignForMenu()
        Dim vTop As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align top", AddressOf Menu_AlignTop, True, menuVAlign = Rhino.DocObjects.TextVerticalAlignment.Top)
        vTop.ToolTipText = "Anchor text above the tag point."

        Dim vMiddle As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align middle", AddressOf Menu_AlignMiddleV, True, menuVAlign = Rhino.DocObjects.TextVerticalAlignment.Middle)
        vMiddle.ToolTipText = "Anchor text vertically centered on the tag point."

        Dim vBottom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align bottom", AddressOf Menu_AlignBottom, True, menuVAlign = Rhino.DocObjects.TextVerticalAlignment.Bottom)
        vBottom.ToolTipText = "Anchor text below the tag point."

        Menu_AppendSeparator(menu)

        Dim justifyLines As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Justify multiline lines", AddressOf Menu_JustifyMultilineLines, True, MenuBoolChecked(JustifyMultilineLines, ZuiOptionalKind.JustifyMultiline))
        justifyLines.ToolTipText = "When text has multiple lines, align each line within the block (shorter lines shift to the chosen horizontal side) instead of only moving the whole block relative to the dot."
    End Sub

    Private Sub Menu_LockUnselected()
        RecordUndoEvent("Text Tag Lock Unselected", New TextTagUndo(Me))
        LockUnselected = Not LockUnselected
        SyncMouse()
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Text Tag List Cache", New TextTagUndo(Me))
        PreserveChanges = Not PreserveChanges
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Text Tag Proximity", New TextTagUndo(Me))
        ProximityCache = Not ProximityCache
        If ProximityCache Then SaveShifted = True
        Me.ExpireSolution(True)
    End Sub

    Public Sub Menu_ClearCache()
        RecordUndoEvent("Text Tag Clear Cache", New TextTagUndo(Me))
        ClearTextCacheInternal()
    End Sub

    Private Sub Menu_AlignLeft()
        SetTextAlignment(Rhino.DocObjects.TextHorizontalAlignment.Left, VerticalAlign)
    End Sub

    Private Sub Menu_AlignMiddle()
        SetTextAlignment(Rhino.DocObjects.TextHorizontalAlignment.Center, VerticalAlign)
    End Sub

    Private Sub Menu_AlignRight()
        SetTextAlignment(Rhino.DocObjects.TextHorizontalAlignment.Right, VerticalAlign)
    End Sub

    Private Sub Menu_AlignTop()
        SetTextAlignment(HorizontalAlign, Rhino.DocObjects.TextVerticalAlignment.Top)
    End Sub

    Private Sub Menu_AlignMiddleV()
        SetTextAlignment(HorizontalAlign, Rhino.DocObjects.TextVerticalAlignment.Middle)
    End Sub

    Private Sub Menu_AlignBottom()
        SetTextAlignment(HorizontalAlign, Rhino.DocObjects.TextVerticalAlignment.Bottom)
    End Sub

    Private Sub SetTextAlignment(h As Rhino.DocObjects.TextHorizontalAlignment, v As Rhino.DocObjects.TextVerticalAlignment)
        If HorizontalAlign = h AndAlso VerticalAlign = v Then Return
        RecordUndoEvent("Text Tag Alignment", New TextTagUndo(Me))
        HorizontalAlign = h
        VerticalAlign = v
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub Menu_JustifyMultilineLines()
        RecordUndoEvent("Text Tag Justify Lines", New TextTagUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.JustifyMultiline, Not (HasZuiInput(ZuiOptionalKind.JustifyMultiline) OrElse JustifyMultilineLines))
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

#End Region

#Region "Optional inputs / ZUI"

    Private Function FindInputIndexByNick(nick As String) As Integer
        For i As Integer = 0 To Params.Input.Count - 1
            If String.Equals(Params.Input(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Function FindActiveInputIndex() As Integer
        Return FindInputIndexByNick("Ac")
    End Function

    Private Shared Function NickNameForZuiKind(kind As ZuiOptionalKind) As String
        Select Case kind
            Case ZuiOptionalKind.Size : Return "S"
            Case ZuiOptionalKind.Colour : Return "C"
            Case ZuiOptionalKind.Font : Return "Fn"
            Case ZuiOptionalKind.Text : Return "Tx"
            Case ZuiOptionalKind.Active : Return "Ac"
            Case ZuiOptionalKind.LockUnselected : Return "Lu"
            Case ZuiOptionalKind.PreserveChanges : Return "Pr"
            Case ZuiOptionalKind.ProximityCache : Return "Px"
            Case ZuiOptionalKind.SaveShifted : Return "Ss"
            Case ZuiOptionalKind.ClearCache : Return "Cc"
            Case ZuiOptionalKind.JustifyMultiline : Return "Jl"
            Case ZuiOptionalKind.HorizontalAlign : Return "Ha"
            Case ZuiOptionalKind.VerticalAlign : Return "Va"
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

    Private Function CreateZuiParam(kind As ZuiOptionalKind) As IGH_Param
        Select Case kind
            Case ZuiOptionalKind.Size
                Return New Grasshopper.Kernel.Parameters.Param_Number With {
                    .Optional = True,
                    .Name = "Size",
                    .NickName = "S",
                    .Description = "Text height per tag in model units (tree paths match P).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Colour
                Return New Grasshopper.Kernel.Parameters.Param_Colour With {
                    .Optional = True,
                    .Name = "Colour",
                    .NickName = "C",
                    .Description = "Dot and text colour per tag (tree paths match P).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Font
                Return New Grasshopper.Kernel.Parameters.Param_String With {
                    .Optional = True,
                    .Name = "Font",
                    .NickName = "Fn",
                    .Description = "Font face name per tag (tree paths match P), e.g. Arial or Helvetica.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Text
                Return New Grasshopper.Kernel.Parameters.Param_String With {
                    .Optional = True,
                    .Name = "Text",
                    .NickName = "Tx",
                    .Description = "Optional preset text per location (matches the location tree). Viewport edits override.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.Active
                Return CreateBoolZuiParam("Active", "Ac",
                    "When true, viewport picking is enabled for that tag (overrides Lock unselected). Tree paths match P.",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.LockUnselected
                Return CreateBoolZuiParam("Lock unselected", "Lu",
                    "When true, viewport picking works only while this component is selected on the Grasshopper canvas. Overridden by Active when that input is present.")
            Case ZuiOptionalKind.PreserveChanges
                Return CreateBoolZuiParam("List cache", "Pr",
                    "Keep entered text by tree path / list index when locations move. With Proximity: keep by index for far moves; proximity remaps wrap-shifts and tree changes.")
            Case ZuiOptionalKind.ProximityCache
                Return CreateBoolZuiParam("Proximity cache", "Px",
                    "Re-attach text by nearest cached location on wrap-shifts, culls, grafts, and other list/tree changes. Culled anchors are always saved and restored if they return.")
            Case ZuiOptionalKind.SaveShifted
                ' Legacy ZUI nick Ss — save-shifted is always on whenever Proximity cache is on.
                Return CreateBoolZuiParam("Save shifted", "Ss",
                    "Legacy input; ignored. Save-shifted is always active when Proximity cache is on.")
            Case ZuiOptionalKind.ClearCache
                Return CreateBoolZuiParam("Clear cache", "Cc",
                    "Pulse true to erase all entered text (rising edge only).")
            Case ZuiOptionalKind.JustifyMultiline
                Return CreateBoolZuiParam("Justify multiline lines", "Jl",
                    "When text has multiple lines, align each line within the block (per tag; tree paths match P).",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.HorizontalAlign
                Return New Grasshopper.Kernel.Parameters.Param_Integer With {
                    .Optional = True,
                    .Name = "Horizontal align",
                    .NickName = "Ha",
                    .Description = "Horizontal text anchor per tag (tree paths match P): 0 = left, 1 = center, 2 = right.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.VerticalAlign
                Return New Grasshopper.Kernel.Parameters.Param_Integer With {
                    .Optional = True,
                    .Name = "Vertical align",
                    .NickName = "Va",
                    .Description = "Vertical text anchor per tag (tree paths match P): 0 = top, 1 = middle, 2 = bottom.",
                    .Access = GH_ParamAccess.tree
                }
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
        VariableParameterMaintenance()
        Params.OnParametersChanged()
    End Sub

    Friend Sub SyncOptionalInputsFromFlags()
        EnsureZuiMatchesFlag(ZuiOptionalKind.JustifyMultiline, JustifyMultilineLines)
        VariableParameterMaintenance()
        Params.OnParametersChanged()
    End Sub

    Private Sub SyncFeatureFlagsFromInputs()
        JustifyMultilineLines = HasZuiInput(ZuiOptionalKind.JustifyMultiline)
    End Sub

    Private Sub EnsureZuiMatchesFlag(kind As ZuiOptionalKind, shouldHave As Boolean)
        If shouldHave Then
            If Not HasZuiInput(kind) Then
                Dim param As IGH_Param = CreateZuiParam(kind)
                If param IsNot Nothing Then Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
            End If
        ElseIf HasZuiInput(kind) Then
            Dim ix As Integer = FindInputIndexByNick(NickNameForZuiKind(kind))
            If ix >= 0 Then
                Dim p As IGH_Param = Params.Input(ix)
                p.RemoveAllSources()
                Params.UnregisterInputParameter(p)
            End If
        End If
    End Sub

    Private Function ZuiInputWired(ix As Integer) As Boolean
        If ix < 0 OrElse Params Is Nothing Then Return False
        Dim p As IGH_Param = Params.Input(ix)
        Return p IsNot Nothing AndAlso p.SourceCount > 0
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

    Private Function EffectiveHorizontalAlignForMenu() As Rhino.DocObjects.TextHorizontalAlignment
        Dim haIx As Integer = FindInputIndexByNick("Ha")
        If haIx >= 0 AndAlso ZuiInputWired(haIx) Then
            Return HorizontalAlignFromInt(ReadIntInputVolatile(haIx, CInt(HorizontalAlign)))
        End If
        Return HorizontalAlign
    End Function

    Private Function EffectiveVerticalAlignForMenu() As Rhino.DocObjects.TextVerticalAlignment
        Dim vaIx As Integer = FindInputIndexByNick("Va")
        If vaIx >= 0 AndAlso ZuiInputWired(vaIx) Then
            Return VerticalAlignFromInt(ReadIntInputVolatile(vaIx, CInt(VerticalAlign)))
        End If
        Return VerticalAlign
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

    Private Sub ApplyBoolInput(DA As IGH_DataAccess, ix As Integer, ByRef target As Boolean, defaultIfUnwired As Boolean)
        If ix < 0 Then Return
        If Params.Input(ix).SourceCount > 0 Then
            Dim v As Boolean = defaultIfUnwired
            If DA.GetData(ix, v) Then target = v
        End If
        ' Unwired: keep target (menu toggle / Write-Read state); do not force defaultIfUnwired.
    End Sub

    Private Sub ApplyZuiBooleanInputs(DA As IGH_DataAccess)
        ApplyBoolInput(DA, FindInputIndexByNick("Lu"), LockUnselected, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Pr"), PreserveChanges, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Px"), ProximityCache, False)
        ' Legacy Ss input is ignored; save-shifted is always on with proximity cache.
        If ProximityCache Then SaveShifted = True Else SaveShifted = False
        ApplyBoolInput(DA, FindInputIndexByNick("Jl"), JustifyMultilineLines, JustifyMultilineLines)

        Dim ccIx As Integer = FindInputIndexByNick("Cc")
        If ccIx >= 0 AndAlso Params.Input(ccIx).SourceCount > 0 Then
            Dim pulse As Boolean = False
            If DA.GetData(ccIx, pulse) Then
                If pulse AndAlso Not _clearCacheInputPrev Then ClearTextCacheInternal()
                _clearCacheInputPrev = pulse
            End If
        Else
            _clearCacheInputPrev = False
        End If
    End Sub

    Private Sub ApplyAlignmentFromInputs(DA As IGH_DataAccess)
        If HasZuiInput(ZuiOptionalKind.HorizontalAlign) OrElse HasZuiInput(ZuiOptionalKind.VerticalAlign) Then Return
        Dim haIx As Integer = FindInputIndexByNick("Ha")
        If haIx >= 0 AndAlso Params.Input(haIx).SourceCount > 0 Then
            Dim h As Integer = CInt(HorizontalAlign)
            If DA.GetData(haIx, h) Then
                Select Case h
                    Case 0 : HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Left
                    Case 2 : HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Right
                    Case Else : HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Center
                End Select
            End If
        End If
        Dim vaIx As Integer = FindInputIndexByNick("Va")
        If vaIx >= 0 AndAlso Params.Input(vaIx).SourceCount > 0 Then
            Dim v As Integer = CInt(VerticalAlign)
            If DA.GetData(vaIx, v) Then
                Select Case v
                    Case 0 : VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Top
                    Case 2 : VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Bottom
                    Case Else : VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Middle
                End Select
            End If
        End If
    End Sub

    Private Sub ClearTextCacheInternal()
        Texts.Clear()
        TextUserEdited.Clear()
        ShiftedTextEntries.Clear()
        CacheSlots = Nothing
        CacheSlotPaths = Nothing
        CacheSlotBranchIndices = Nothing
        CacheTreeKeys = Nothing
        CloseTagTextBoxIfAny()
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

    Private Function IsActiveForViewport() As Boolean
        Dim acIx As Integer = FindActiveInputIndex()
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

    Friend Function IsTagActiveForViewport(index As Integer) As Boolean
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
        If FindActiveInputIndex() >= 0 Then Return True
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

#Region "Variable parameters (canvas ZUI)"

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

    ''' <summary>Entered text per item index (persisted in the GH file).</summary>
    Friend Texts As New List(Of String)

    ''' <summary>True when the text at this index was set from the viewport (not driven by the Tx input).</summary>
    Friend TextUserEdited As New List(Of Boolean)

    ''' <summary>Data tree path per flattened slot (parallel to Slots).</summary>
    Friend SlotPaths As New List(Of GH_Path)

    ''' <summary>Index within the input branch at SlotPaths (parallel to Slots).</summary>
    Friend SlotBranchIndices As New List(Of Integer)

    ''' <summary>List cache: keep texts by tree path / list index when locations move (on by default). With ProximityCache: mixed mode.</summary>
    Public PreserveChanges As Boolean = True

    ''' <summary>Proximity cache: remap by nearest cached location when the list/tree structure changes. Save-shifted is always on with this flag.</summary>
    Public ProximityCache As Boolean = False

    ''' <summary>Always mirrors ProximityCache (culled anchors banked/restored). Kept for serialization / undo compatibility.</summary>
    Public SaveShifted As Boolean = False

    ''' <summary>When true, viewport interaction requires the component to be selected on the canvas.</summary>
    Public LockUnselected As Boolean = True

    ''' <summary>Horizontal text anchor at the tag point (default: center).</summary>
    Public HorizontalAlign As Rhino.DocObjects.TextHorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center

    ''' <summary>Vertical text anchor at the tag point (default: middle).</summary>
    Public VerticalAlign As Rhino.DocObjects.TextVerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Middle

    ''' <summary>When true, multiline text aligns each line within the block (shorter lines shift to the horizontal alignment side).</summary>
    Public JustifyMultilineLines As Boolean = False

    ''' <summary>Anchors from the last solve (world locations plus optional planes).</summary>
    Friend Slots As New List(Of TextTagSlot)

    ''' <summary>Cached anchors used to detect upstream changes when PreserveChanges is off.</summary>
    Private CacheSlots As List(Of TextTagSlot) = Nothing

    ''' <summary>Cached input paths / branch indices aligned with CacheSlots.</summary>
    Private CacheSlotPaths As List(Of GH_Path) = Nothing
    Private CacheSlotBranchIndices As List(Of Integer) = Nothing

    ''' <summary>Stable tree-structure fingerprint (path + branch index per slot), updated after every cache write including proximity remaps.</summary>
    Private CacheTreeKeys As List(Of String) = Nothing

    ''' <summary>Text saved for anchors that left the input list (Save shifted).</summary>
    Friend ShiftedTextEntries As New List(Of ShiftedTextEntry)

    Friend TextHeight As Double = 1.0R
    Friend FontFace As String = String.Empty
    Friend TagColour As Color = Color.Black

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

    Private Shared Sub ApplyFontToText3d(t As Text3d, fontFace As String)
        If t Is Nothing OrElse String.IsNullOrWhiteSpace(fontFace) Then Return
        t.FontFace = fontFace.Trim()
    End Sub

    Friend Function TagColourForIndex(index As Integer) As Color
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length AndAlso SlotSettings(index).HasCustomColour Then
            Return SlotSettings(index).TagColour
        End If
        Return TagColour
    End Function

    Friend Function HorizontalAlignForIndex(index As Integer) As Rhino.DocObjects.TextHorizontalAlignment
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return SlotSettings(index).HorizontalAlign
        End If
        Return HorizontalAlign
    End Function

    Friend Function VerticalAlignForIndex(index As Integer) As Rhino.DocObjects.TextVerticalAlignment
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return SlotSettings(index).VerticalAlign
        End If
        Return VerticalAlign
    End Function

    Friend Function JustifyMultilineForIndex(index As Integer) As Boolean
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return SlotSettings(index).JustifyMultilineLines
        End If
        Return JustifyMultilineLines
    End Function

    Friend TagMouse As TextTagMouse
    Friend TagTextBox As FormTextTagBox = Nothing
    Friend HoverIndex As Integer = -1
    ''' <summary>Viewport text scale while a tag is hovered.</summary>
    Private Const HoverTextScale As Double = 1.15R
    ''' <summary>Pixel pick radius around an empty tag anchor dot.</summary>
    Friend Const TagPickRadiusPx As Double = 14.0R
    ''' <summary>Slot index currently being edited in the floating text box (-1 = none).</summary>
    Friend EditIndex As Integer = -1

    Private _clearCacheInputPrev As Boolean = False

    Friend Sub SetTagTextsFromUndo(newTexts As List(Of String), newEdited As List(Of Boolean), newPreserve As Boolean, newProximity As Boolean,
                                   newSaveShifted As Boolean, newShifted As List(Of ShiftedTextEntry),
                                   newHAlign As Rhino.DocObjects.TextHorizontalAlignment, newVAlign As Rhino.DocObjects.TextVerticalAlignment,
                                   newJustifyLines As Boolean, newLockUnselected As Boolean)
        Texts = New List(Of String)(newTexts)
        TextUserEdited = New List(Of Boolean)(newEdited)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        SaveShifted = newProximity
        ShiftedTextEntries = CloneShiftedTextList(newShifted)
        HorizontalAlign = newHAlign
        VerticalAlign = newVAlign
        JustifyMultilineLines = newJustifyLines
        LockUnselected = newLockUnselected
        CloseTagTextBoxIfAny()
        SyncMouse()
        Me.ExpireSolution(True)
    End Sub

    Friend Sub CloseTagTextBoxIfAny()
        If TagTextBox Is Nothing Then Return
        Dim tb As FormTextTagBox = TagTextBox
        TagTextBox = Nothing
        EditIndex = -1
        tb.DismissWithoutCommit()
    End Sub

    Friend Sub ForgetFloatingTagTextBox()
        TagTextBox = Nothing
    End Sub

    Friend Sub CancelPendingTextInput()
        EditIndex = -1
    End Sub

    ''' <summary>Commit from the floating box: store text (empty clears it, dot returns) and refresh outputs.</summary>
    Friend Sub CommitTagText(index As Integer, value As String)
        If index < 0 OrElse index >= Slots.Count Then Return
        While Texts.Count < Slots.Count
            Texts.Add(String.Empty)
        End While
        Dim clean As String = If(value, String.Empty).TrimEnd()
        clean = clean.TrimStart()
        If String.Equals(Texts(index), clean, StringComparison.Ordinal) Then
            EditIndex = -1
            Return
        End If
        RecordUndoEvent("Text Tag Edit", New TextTagUndo(Me))
        Texts(index) = clean
        While TextUserEdited.Count < Texts.Count
            TextUserEdited.Add(False)
        End While
        TextUserEdited(index) = True
        EditIndex = -1
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub ShutDownInteraction()
        CloseTagTextBoxIfAny()
        If TagMouse IsNot Nothing Then TagMouse.Enabled = False
    End Sub

    ''' <summary>Viewport clicks are live when unlocked, previewed, has anchors, and selection rules allow it.</summary>
    Friend Sub SyncMouse()
        Dim selectionOk As Boolean = IsSelectionAllowedForViewport()
        Dim want As Boolean =
            selectionOk AndAlso
            Not Me.Locked AndAlso
            ViewportPreview.IsEffectivelyPreviewed(Me) AndAlso
            Slots.Count > 0
        If TagMouse IsNot Nothing Then
            If TagMouse.Enabled <> want Then TagMouse.Enabled = want
            TagMouse.SetHoverPollActive(want)
        End If
        If Not want Then CloseTagTextBoxIfAny()
        If Not want AndAlso HoverIndex >= 0 Then
            HoverIndex = -1
            Try
                Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
            Catch
            End Try
        End If
    End Sub

    Friend Sub SetHoverIndex(index As Integer)
        If index < -1 Then index = -1
        If HoverIndex = index Then Return
        HoverIndex = index
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

#End Region

#Region "Solve"

    Private Shared Function SlotsEqual(a As List(Of TextTagSlot), b As List(Of TextTagSlot)) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Count <> b.Count Then Return False
        Const tol As Double = 0.0001
        Const ang As Double = 0.002
        For i As Integer = 0 To a.Count - 1
            If a(i).HasPlane <> b(i).HasPlane Then Return False
            If a(i).Location.DistanceTo(b(i).Location) > tol Then Return False
            If a(i).HasPlane Then
                If a(i).Plane.ZAxis.IsParallelTo(b(i).Plane.ZAxis, ang) <> 1 Then Return False
                If a(i).Plane.XAxis.IsParallelTo(b(i).Plane.XAxis, ang) <> 1 Then Return False
            End If
        Next
        Return True
    End Function

    Private Shared Function PathsEqual(a As GH_Path, b As GH_Path) As Boolean
        If a Is Nothing AndAlso b Is Nothing Then Return True
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Length <> b.Length Then Return False
        For i As Integer = 0 To a.Length - 1
            If a(i) <> b(i) Then Return False
        Next
        Return True
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

    Private Sub StoreLocationCache(slots As List(Of TextTagSlot), paths As List(Of GH_Path), branch As List(Of Integer))
        CacheSlots = CloneSlotList(slots)
        CacheSlotPaths = ClonePathList(paths)
        CacheSlotBranchIndices = If(branch Is Nothing, New List(Of Integer), New List(Of Integer)(branch))
        CacheTreeKeys = BuildTreeKeys(CacheSlotPaths, CacheSlotBranchIndices)
    End Sub

    Private Sub ProtectNonEmptyTextsAsEdited()
        While TextUserEdited.Count < Texts.Count
            TextUserEdited.Add(False)
        End While
        For i As Integer = 0 To Texts.Count - 1
            If Not String.IsNullOrEmpty(Texts(i)) Then TextUserEdited(i) = True
        Next
    End Sub

    Private Shared Function SlotMetadataEqual(aPaths As List(Of GH_Path), aBranch As List(Of Integer),
                                              bPaths As List(Of GH_Path), bBranch As List(Of Integer)) As Boolean
        Return TreeKeysEqual(BuildTreeKeys(aPaths, aBranch), BuildTreeKeys(bPaths, bBranch))
    End Function

    Private Shared Function SlotLocations(slots As List(Of TextTagSlot)) As List(Of Point3d)
        Dim pts As New List(Of Point3d)
        If slots Is Nothing Then Return pts
        For Each s As TextTagSlot In slots
            pts.Add(If(s.Location.IsValid, s.Location, Point3d.Unset))
        Next
        Return pts
    End Function

    ''' <summary>True when proximity matching agrees with list indices (or finds no matches — far moves).</summary>
    Private Shared Function PreferListKeepByProximityIdentity(oldSlots As List(Of TextTagSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                                              newSlots As List(Of TextTagSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As Boolean
        Dim slotMap As Integer() = ProximityMatching.BuildCenterSlotMap(
            SlotLocations(oldSlots), SlotLocations(newSlots), oldPaths, oldBranch, newPaths, newBranch, requireMatchingPaths:=False)
        Return ProximityMatching.SlotMapIsIndexIdentity(slotMap)
    End Function

    Private Shared Function SlotsChanged(oldSlots As List(Of TextTagSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                         newSlots As List(Of TextTagSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As Boolean
        If oldSlots Is Nothing OrElse newSlots Is Nothing Then Return True
        If oldSlots.Count <> newSlots.Count Then Return True
        If Not SlotMetadataEqual(oldPaths, oldBranch, newPaths, newBranch) Then Return True
        Return Not SlotsEqual(oldSlots, newSlots)
    End Function

    Private Shared Function ModelAbsoluteTolerance() As Double
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then Return doc.ModelAbsoluteTolerance
        Catch
        End Try
        Return 0.001
    End Function

    Private Shared Function TryGetProximityKey(slot As TextTagSlot, ByRef key As TextTagProximityKey) As Boolean
        key = New TextTagProximityKey
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

    Private Shared Function ProximityKeysSimilar(a As TextTagProximityKey, b As TextTagProximityKey, tol As Double) As Boolean
        If a.HasPlane <> b.HasPlane Then Return False
        If Not a.Location.IsValid OrElse Not b.Location.IsValid Then Return False
        If a.Location.DistanceTo(b.Location) > tol Then Return False
        If a.HasPlane Then
            Dim az As New Vector3d(a.PlaneZx, a.PlaneZy, a.PlaneZz)
            Dim bz As New Vector3d(b.PlaneZx, b.PlaneZy, b.PlaneZz)
            If az.IsParallelTo(bz, 0.002) <> 1 Then Return False
        End If
        Return True
    End Function

    Private Shared Function MaxProximityMatchDistance() As Double
        Return Math.Max(ModelAbsoluteTolerance() * 10.0R, 0.01R)
    End Function

    Private Shared Function IsWithinProximityMatch(a As TextTagProximityKey, b As TextTagProximityKey) As Boolean
        If a.HasPlane <> b.HasPlane Then Return False
        If Not a.Location.IsValid OrElse Not b.Location.IsValid Then Return False
        Return a.Location.DistanceTo(b.Location) <= MaxProximityMatchDistance()
    End Function

    Private Shared Function ProximityMatchScore(oldKey As TextTagProximityKey, newKey As TextTagProximityKey) As Double
        If oldKey.HasPlane <> newKey.HasPlane Then Return Double.PositiveInfinity
        If Not oldKey.Location.IsValid OrElse Not newKey.Location.IsValid Then Return Double.PositiveInfinity
        Return oldKey.Location.DistanceToSquared(newKey.Location)
    End Function

    Private Shared Function ShiftedKeyMatchesCandidate(saved As TextTagProximityKey, candidate As TextTagProximityKey) As Boolean
        Return IsWithinProximityMatch(saved, candidate)
    End Function

    Private Shared Function CloneShiftedTextList(src As List(Of ShiftedTextEntry)) As List(Of ShiftedTextEntry)
        If src Is Nothing Then Return New List(Of ShiftedTextEntry)
        Return New List(Of ShiftedTextEntry)(src)
    End Function

    Private Shared Function CloneSlotList(src As List(Of TextTagSlot)) As List(Of TextTagSlot)
        If src Is Nothing Then Return Nothing
        Return New List(Of TextTagSlot)(src)
    End Function

    Private Sub AddShiftedTextEntry(key As TextTagProximityKey, text As String, userEdited As Boolean)
        For Each existing As ShiftedTextEntry In ShiftedTextEntries
            If ShiftedKeyMatchesCandidate(existing.Key, key) Then Return
        Next
        Dim entry As New ShiftedTextEntry With {
            .Key = key,
            .Text = If(text, String.Empty),
            .UserEdited = userEdited
        }
        ShiftedTextEntries.Add(entry)
    End Sub

    Private Sub RemoveShiftedEntriesMatching(key As TextTagProximityKey)
        ShiftedTextEntries.RemoveAll(Function(e) ShiftedKeyMatchesCandidate(e.Key, key))
    End Sub

    Private Shared Function OldSlotStillInList(oldSlots As List(Of TextTagSlot), oi As Integer, newSlots As List(Of TextTagSlot)) As Boolean
        Dim ka As TextTagProximityKey = Nothing
        If Not TryGetProximityKey(oldSlots(oi), ka) Then Return False
        For ni As Integer = 0 To newSlots.Count - 1
            Dim kb As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(newSlots(ni), kb) Then Continue For
            If IsWithinProximityMatch(ka, kb) Then Return True
        Next
        Return False
    End Function

    Private Sub RememberShiftedTexts(oldSlots As List(Of TextTagSlot), prevTexts As List(Of String), prevEdited As List(Of Boolean),
                                     newSlots As List(Of TextTagSlot))
        Dim nOld As Integer = Math.Min(oldSlots.Count, prevTexts.Count)
        For oi As Integer = 0 To nOld - 1
            Dim hadText As Boolean = oi < prevTexts.Count AndAlso Not String.IsNullOrEmpty(prevTexts(oi))
            Dim userEdited As Boolean = oi < prevEdited.Count AndAlso prevEdited(oi)
            If Not hadText AndAlso Not userEdited Then Continue For

            Dim ka As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(oldSlots(oi), ka) Then Continue For

            If OldSlotStillInList(oldSlots, oi, newSlots) Then
                RemoveShiftedEntriesMatching(ka)
            Else
                Dim txt As String = If(oi < prevTexts.Count, prevTexts(oi), String.Empty)
                AddShiftedTextEntry(ka, txt, userEdited)
            End If
        Next
    End Sub

    Private Sub ApplyShiftedTexts(slots As List(Of TextTagSlot), texts As List(Of String), edited As List(Of Boolean))
        If ShiftedTextEntries.Count = 0 Then Return

        Dim usedSaved As New HashSet(Of Integer)
        For ni As Integer = 0 To slots.Count - 1
            Dim kb As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(slots(ni), kb) Then Continue For

            For si As Integer = 0 To ShiftedTextEntries.Count - 1
                If usedSaved.Contains(si) Then Continue For
                If Not ShiftedKeyMatchesCandidate(ShiftedTextEntries(si).Key, kb) Then Continue For

                While texts.Count <= ni
                    texts.Add(String.Empty)
                End While
                While edited.Count <= ni
                    edited.Add(False)
                End While

                Dim saved As ShiftedTextEntry = ShiftedTextEntries(si)
                If String.IsNullOrEmpty(texts(ni)) OrElse Not edited(ni) Then
                    texts(ni) = saved.Text
                    edited(ni) = saved.UserEdited
                End If
                usedSaved.Add(si)
                Exit For
            Next
        Next

        If usedSaved.Count > 0 Then
            Dim remaining As New List(Of ShiftedTextEntry)
            For si As Integer = 0 To ShiftedTextEntries.Count - 1
                If Not usedSaved.Contains(si) Then remaining.Add(ShiftedTextEntries(si))
            Next
            ShiftedTextEntries = remaining
        End If
    End Sub

    ''' <summary>Path-aware proximity remap: same-index pass then greedy nearest within tolerance.
    ''' When the tree address sequence is unchanged, keeps values by list index even if locations moved (list-cache identity).</summary>
    Private Shared Sub RemapTextsByProximity(oldSlots As List(Of TextTagSlot), oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                             oldTexts As List(Of String), oldEdited As List(Of Boolean),
                                             newSlots As List(Of TextTagSlot), newPaths As List(Of GH_Path), newBranch As List(Of Integer),
                                             newTexts As List(Of String), newEdited As List(Of Boolean))
        newTexts.Clear()
        newEdited.Clear()
        For i As Integer = 0 To newSlots.Count - 1
            newTexts.Add(String.Empty)
            newEdited.Add(False)
        Next

        Dim usedOld As New HashSet(Of Integer)
        Dim usedNew As New HashSet(Of Integer)
        Dim nOld As Integer = Math.Min(oldSlots.Count, oldTexts.Count)
        Const sameIndexTol As Double = 0.0001
        Dim indexTol As Double = Math.Max(sameIndexTol, ModelAbsoluteTolerance())

        ' Pass 1: same flat index when path, branch index, and location still match.
        Dim sameIndexLimit As Integer = Math.Min(nOld, newSlots.Count)
        For i As Integer = 0 To sameIndexLimit - 1
            If String.IsNullOrEmpty(oldTexts(i)) AndAlso (i >= oldEdited.Count OrElse Not oldEdited(i)) Then Continue For
            If usedOld.Contains(i) OrElse usedNew.Contains(i) Then Continue For
            If oldPaths IsNot Nothing AndAlso newPaths IsNot Nothing AndAlso i < oldPaths.Count AndAlso i < newPaths.Count Then
                If Not PathsEqual(oldPaths(i), newPaths(i)) Then Continue For
            End If
            If oldBranch IsNot Nothing AndAlso newBranch IsNot Nothing AndAlso i < oldBranch.Count AndAlso i < newBranch.Count Then
                If oldBranch(i) <> newBranch(i) Then Continue For
            End If
            Dim ka As TextTagProximityKey = Nothing
            Dim kb As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(oldSlots(i), ka) Then Continue For
            If Not TryGetProximityKey(newSlots(i), kb) Then Continue For
            If ProximityKeysSimilar(ka, kb, indexTol) Then
                newTexts(i) = oldTexts(i)
                newEdited(i) = i < oldEdited.Count AndAlso oldEdited(i)
                usedOld.Add(i)
                usedNew.Add(i)
            End If
        Next

        ' Pass 2: greedy nearest match for remaining items with text.
        Dim pairs As New List(Of Tuple(Of Double, Integer, Integer))
        For oi As Integer = 0 To nOld - 1
            If usedOld.Contains(oi) Then Continue For
            If String.IsNullOrEmpty(oldTexts(oi)) AndAlso (oi >= oldEdited.Count OrElse Not oldEdited(oi)) Then Continue For
            Dim ka As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(oldSlots(oi), ka) Then Continue For
            For ni As Integer = 0 To newSlots.Count - 1
                If usedNew.Contains(ni) Then Continue For
                Dim kb As TextTagProximityKey = Nothing
                If Not TryGetProximityKey(newSlots(ni), kb) Then Continue For
                Dim score As Double = ProximityMatchScore(ka, kb)
                If Double.IsPositiveInfinity(score) Then Continue For
                If Not IsWithinProximityMatch(ka, kb) Then Continue For
                pairs.Add(Tuple.Create(score, oi, ni))
            Next
        Next
        pairs.Sort(Function(x, y) x.Item1.CompareTo(y.Item1))

        For Each p As Tuple(Of Double, Integer, Integer) In pairs
            If usedOld.Contains(p.Item2) OrElse usedNew.Contains(p.Item3) Then Continue For
            Dim ka As TextTagProximityKey = Nothing
            Dim kb As TextTagProximityKey = Nothing
            If Not TryGetProximityKey(oldSlots(p.Item2), ka) Then Continue For
            If Not TryGetProximityKey(newSlots(p.Item3), kb) Then Continue For
            If Not IsWithinProximityMatch(ka, kb) Then Continue For
            newTexts(p.Item3) = oldTexts(p.Item2)
            newEdited(p.Item3) = p.Item2 < oldEdited.Count AndAlso oldEdited(p.Item2)
            usedOld.Add(p.Item2)
            usedNew.Add(p.Item3)
        Next
    End Sub

    Private Shared Function TryParseLocationGoo(g As IGH_GeometricGoo, ByRef slot As TextTagSlot) As Boolean
        slot = New TextTagSlot
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

    Private Shared Function TextFromStringItem(gs As GH_String) As String
        If gs Is Nothing Then Return String.Empty
        Return If(gs.Value, String.Empty)
    End Function

    Private Function HasTextInputConnected() As Boolean
        Dim txIx As Integer = FindInputIndexByNick("Tx")
        If txIx < 0 Then Return False
        Return Me.Params.Input(txIx).SourceCount > 0
    End Function

    Private Shared Sub BuildSlotsFromTree(locData As GH_Structure(Of IGH_GeometricGoo), newSlots As List(Of TextTagSlot), newPaths As List(Of GH_Path), newBranchIndices As List(Of Integer))
        newSlots.Clear()
        newPaths.Clear()
        newBranchIndices.Clear()
        For Each path As GH_Path In locData.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = locData.DataList(path)
            For j As Integer = 0 To branch.Count - 1
                Dim g As IGH_GeometricGoo = branch(j)
                Dim slot As TextTagSlot
                If TryParseLocationGoo(g, slot) Then
                    newSlots.Add(slot)
                    newPaths.Add(path)
                    newBranchIndices.Add(j)
                End If
            Next
        Next
    End Sub

    Private Shared Sub BuildInputTextsFromTrees(locData As GH_Structure(Of IGH_GeometricGoo), textData As GH_Structure(Of GH_String), result As List(Of String))
        result.Clear()
        Dim broadcast As String = String.Empty
        Dim broadcastSet As Boolean = False
        If textData IsNot Nothing AndAlso textData.DataCount = 1 AndAlso textData.Paths.Count > 0 Then
            broadcast = TextFromStringItem(textData.DataList(textData.Paths(0))(0))
            broadcastSet = True
        End If

        For Each path As GH_Path In locData.Paths
            Dim locBranch = locData.DataList(path)
            Dim textBranch As IList(Of GH_String) = Nothing
            If textData IsNot Nothing AndAlso textData.PathExists(path) Then
                textBranch = textData.DataList(path)
            End If
            For j As Integer = 0 To locBranch.Count - 1
                Dim g As IGH_GeometricGoo = locBranch(j)
                Dim slot As TextTagSlot
                If Not TryParseLocationGoo(g, slot) Then Continue For

                Dim txt As String = String.Empty
                If textBranch IsNot Nothing AndAlso j < textBranch.Count Then
                    txt = TextFromStringItem(textBranch(j))
                ElseIf broadcastSet Then
                    txt = broadcast
                End If
                result.Add(txt)
            Next
        Next
    End Sub

    Private Shared Sub ForEachValidLocationSlot(locData As GH_Structure(Of IGH_GeometricGoo), action As Action(Of Integer, GH_Path, Integer))
        Dim flat As Integer = 0
        For Each path As GH_Path In locData.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = locData.DataList(path)
            For j As Integer = 0 To branch.Count - 1
                Dim slot As TextTagSlot
                If TryParseLocationGoo(branch(j), slot) Then
                    action(flat, path, j)
                    flat += 1
                End If
            Next
        Next
    End Sub

#Region "Per-tag slot settings (tree-matched optional inputs)"

    Private Shared Function HorizontalAlignFromInt(h As Integer) As Rhino.DocObjects.TextHorizontalAlignment
        Select Case h
            Case 0 : Return Rhino.DocObjects.TextHorizontalAlignment.Left
            Case 2 : Return Rhino.DocObjects.TextHorizontalAlignment.Right
            Case Else : Return Rhino.DocObjects.TextHorizontalAlignment.Center
        End Select
    End Function

    Private Shared Function VerticalAlignFromInt(v As Integer) As Rhino.DocObjects.TextVerticalAlignment
        Select Case v
            Case 0 : Return Rhino.DocObjects.TextVerticalAlignment.Top
            Case 2 : Return Rhino.DocObjects.TextVerticalAlignment.Bottom
            Case Else : Return Rhino.DocObjects.TextVerticalAlignment.Middle
        End Select
    End Function

    Private Sub MapBoolTreeToTagSlots(DA As IGH_DataAccess, nick As String, locData As GH_Structure(Of IGH_GeometricGoo),
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

        ForEachValidLocationSlot(locData,
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

    Private Sub MapNumberTreeToTagSlots(DA As IGH_DataAccess, nick As String, locData As GH_Structure(Of IGH_GeometricGoo),
                                        apply As Action(Of Integer, Double))
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

        ForEachValidLocationSlot(locData,
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

    Private Sub MapStringTreeToTagSlots(DA As IGH_DataAccess, nick As String, locData As GH_Structure(Of IGH_GeometricGoo),
                                        apply As Action(Of Integer, String))
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

        ForEachValidLocationSlot(locData,
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

    Private Sub MapColourTreeToTagSlots(DA As IGH_DataAccess, locData As GH_Structure(Of IGH_GeometricGoo))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick("C")
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

        ForEachValidLocationSlot(locData,
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
                If hasCol Then
                    SlotSettings(flat).TagColour = col
                    SlotSettings(flat).HasCustomColour = True
                End If
            End Sub)
    End Sub

    Private Sub MapIntTreeToTagSlots(DA As IGH_DataAccess, nick As String, locData As GH_Structure(Of IGH_GeometricGoo),
                                      apply As Action(Of Integer, Integer))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Integer)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Integer = 0
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gi As GH_Integer = tree.AllData(True).FirstOrDefault()
            If gi IsNot Nothing Then broadcast = gi.Value
        End If

        ForEachValidLocationSlot(locData,
            Sub(flat As Integer, path As GH_Path, j As Integer)
                If flat >= SlotSettings.Length Then Return
                Dim v As Integer = 0
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

    Private Sub BuildSlotSettings(DA As IGH_DataAccess, locData As GH_Structure(Of IGH_GeometricGoo))
        Dim n As Integer = Slots.Count
        If n <= 0 Then
            SlotSettings = Nothing
            Return
        End If

        ReDim SlotSettings(n - 1)
        For i As Integer = 0 To n - 1
            Dim s As TextTagSlotSettings
            s.Active = True
            s.TextHeight = TextHeight
            s.FontFace = FontFace
            s.TagColour = TagColour
            s.HasCustomColour = False
            s.HorizontalAlign = HorizontalAlign
            s.VerticalAlign = VerticalAlign
            s.JustifyMultilineLines = JustifyMultilineLines
            SlotSettings(i) = s
        Next

        If HasZuiInput(ZuiOptionalKind.Active) Then
            MapBoolTreeToTagSlots(DA, "Ac", locData, True, Sub(i, v) SlotSettings(i).Active = v)
        End If
        If HasZuiInput(ZuiOptionalKind.Size) Then
            MapNumberTreeToTagSlots(DA, "S", locData,
                Sub(i, v)
                    If v > 0 AndAlso Not Double.IsNaN(v) Then SlotSettings(i).TextHeight = v
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.Colour) Then
            MapColourTreeToTagSlots(DA, locData)
        End If
        If HasZuiInput(ZuiOptionalKind.Font) Then
            MapStringTreeToTagSlots(DA, "Fn", locData,
                Sub(i, v)
                    If Not String.IsNullOrWhiteSpace(v) Then SlotSettings(i).FontFace = v.Trim()
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.JustifyMultiline) Then
            MapBoolTreeToTagSlots(DA, "Jl", locData, JustifyMultilineLines, Sub(i, v) SlotSettings(i).JustifyMultilineLines = v)
        End If
        If HasZuiInput(ZuiOptionalKind.HorizontalAlign) Then
            MapIntTreeToTagSlots(DA, "Ha", locData, Sub(i, v) SlotSettings(i).HorizontalAlign = HorizontalAlignFromInt(v))
        End If
        If HasZuiInput(ZuiOptionalKind.VerticalAlign) Then
            MapIntTreeToTagSlots(DA, "Va", locData, Sub(i, v) SlotSettings(i).VerticalAlign = VerticalAlignFromInt(v))
        End If
    End Sub

#End Region

    Private Sub ApplyInputTexts(inputTexts As List(Of String))
        If Not HasTextInputConnected() Then Return

        While TextUserEdited.Count < Slots.Count
            TextUserEdited.Add(False)
        End While
        While Texts.Count < Slots.Count
            Texts.Add(String.Empty)
        End While

        For i As Integer = 0 To Slots.Count - 1
            If i < TextUserEdited.Count AndAlso TextUserEdited(i) Then Continue For
            Dim v As String = String.Empty
            If inputTexts IsNot Nothing AndAlso i < inputTexts.Count Then v = inputTexts(i)
            Texts(i) = v
        Next
    End Sub

    Private Sub SetTextOutputTree(DA As IGH_DataAccess)
        Dim outData As New GH_Structure(Of GH_String)
        For i As Integer = 0 To Slots.Count - 1
            Dim path As GH_Path = If(i < SlotPaths.Count, SlotPaths(i), New GH_Path(0))
            Dim txt As String = If(i < Texts.Count, Texts(i), String.Empty)
            outData.Append(New GH_String(txt), path)
        Next
        DA.SetDataTree(0, outData)
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim locData As New GH_Structure(Of IGH_GeometricGoo)
        If Not DA.GetDataTree(0, locData) Then
            Slots.Clear()
            SlotPaths.Clear()
            SlotBranchIndices.Clear()
            CacheSlots = Nothing
            CacheSlotPaths = Nothing
            CacheSlotBranchIndices = Nothing
            CacheTreeKeys = Nothing
            SyncMouse()
            Exit Sub
        End If

        Dim size As Double = 1.0R
        Dim sizeIx As Integer = FindInputIndexByNick("S")
        If sizeIx >= 0 AndAlso Not HasZuiInput(ZuiOptionalKind.Size) Then
            DA.GetData(sizeIx, size)
        End If
        If size <= 0 OrElse Double.IsNaN(size) Then size = 1.0R
        TextHeight = size

        Dim col As Color = Color.Black
        Dim colIx As Integer = FindInputIndexByNick("C")
        If colIx >= 0 AndAlso Not HasZuiInput(ZuiOptionalKind.Colour) Then
            DA.GetData(colIx, col)
        End If
        TagColour = col

        ApplyZuiBooleanInputs(DA)
        ApplyAlignmentFromInputs(DA)

        Dim newSlots As New List(Of TextTagSlot)
        Dim newPaths As New List(Of GH_Path)
        Dim newBranchIndices As New List(Of Integer)
        BuildSlotsFromTree(locData, newSlots, newPaths, newBranchIndices)
        Dim skipped As Integer = locData.DataCount - newSlots.Count
        If skipped > 0 Then
            Me.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, skipped.ToString() & " location(s) could not be read as a point or plane and were skipped.")
        End If

        Dim inputTexts As New List(Of String)
        If HasTextInputConnected() Then
            Dim textData As New GH_Structure(Of GH_String)
            Dim txIx As Integer = FindInputIndexByNick("Tx")
            If txIx >= 0 AndAlso DA.GetDataTree(txIx, textData) Then
                BuildInputTextsFromTrees(locData, textData, inputTexts)
            End If
        End If

        If CacheSlots Is Nothing OrElse CacheTreeKeys Is Nothing Then
            StoreLocationCache(newSlots, newPaths, newBranchIndices)
            If ProximityCache Then
                ApplyShiftedTexts(newSlots, Texts, TextUserEdited)
            End If
        ElseIf SlotsChanged(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, newSlots, newPaths, newBranchIndices) Then
            Dim newTreeKeys As List(Of String) = BuildTreeKeys(newPaths, newBranchIndices)
            Dim treeChanged As Boolean = Not TreeKeysEqual(CacheTreeKeys, newTreeKeys)
            Dim preferIndexKeep As Boolean = PreserveChanges AndAlso Not treeChanged AndAlso
                (Not ProximityCache OrElse PreferListKeepByProximityIdentity(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, newSlots, newPaths, newBranchIndices))

            If preferIndexKeep Then
                ' List cache: same tree addresses and not a wrap-shift/reorder — keep texts; refresh centroids below.
                ProtectNonEmptyTextsAsEdited()
            ElseIf ProximityCache Then
                ' Proximity (+ save-shifted): tree changes, wrap-shifts, culls, grafts.
                Dim prevTexts As New List(Of String)(Texts)
                Dim prevEdited As New List(Of Boolean)(TextUserEdited)
                Dim remappedTexts As New List(Of String)
                Dim remappedEdited As New List(Of Boolean)
                RemapTextsByProximity(CacheSlots, CacheSlotPaths, CacheSlotBranchIndices, Texts, TextUserEdited,
                                      newSlots, newPaths, newBranchIndices, remappedTexts, remappedEdited)
                Texts = remappedTexts
                TextUserEdited = remappedEdited
                RememberShiftedTexts(CacheSlots, prevTexts, prevEdited, newSlots)
                ApplyShiftedTexts(newSlots, Texts, TextUserEdited)
                ProtectNonEmptyTextsAsEdited()
            ElseIf Not PreserveChanges Then
                Texts.Clear()
                TextUserEdited.Clear()
            End If
            ' Always refresh centroids + remembered tree structure after any change (including post-proximity).
            StoreLocationCache(newSlots, newPaths, newBranchIndices)
        ElseIf ProximityCache Then
            ApplyShiftedTexts(newSlots, Texts, TextUserEdited)
        End If

        Slots = newSlots
        SlotPaths = newPaths
        SlotBranchIndices = newBranchIndices
        BuildSlotSettings(DA, locData)

        While Texts.Count < Slots.Count
            Texts.Add(String.Empty)
        End While
        While TextUserEdited.Count < Slots.Count
            TextUserEdited.Add(False)
        End While
        If Not PreserveChanges AndAlso Texts.Count > Slots.Count Then
            Texts.RemoveRange(Slots.Count, Texts.Count - Slots.Count)
            TextUserEdited.RemoveRange(Slots.Count, TextUserEdited.Count - Slots.Count)
        End If

        ApplyInputTexts(inputTexts)

        If EditIndex >= Slots.Count Then CloseTagTextBoxIfAny()

        SetTextOutputTree(DA)
        SyncMouse()
    End Sub

#End Region

#Region "Preview"

    Private Shared Function TextHasMultipleLines(text As String) As Boolean
        If String.IsNullOrEmpty(text) Then Return False
        Return text.IndexOfAny(New Char() {ChrW(10), ChrW(13)}) >= 0
    End Function

    Private Shared Function SplitTextLines(text As String) As String()
        Return text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(New Char() {ChrW(10)}, StringSplitOptions.None)
    End Function

    Private Structure TextLineMetrics
        Public Width As Double
        Public Height As Double
    End Structure

    Private Shared Sub BoundingBoxPlaneExtents(bb As BoundingBox, pl As Plane, ByRef minX As Double, ByRef maxX As Double, ByRef minY As Double, ByRef maxY As Double)
        minX = Double.PositiveInfinity
        maxX = Double.NegativeInfinity
        minY = Double.PositiveInfinity
        maxY = Double.NegativeInfinity
        If Not bb.IsValid Then Return
        For Each c As Point3d In bb.GetCorners()
            Dim lx As Double = (c - pl.Origin) * pl.XAxis
            Dim ly As Double = (c - pl.Origin) * pl.YAxis
            minX = Math.Min(minX, lx)
            maxX = Math.Max(maxX, lx)
            minY = Math.Min(minY, ly)
            maxY = Math.Max(maxY, ly)
        Next
    End Sub

    Private Shared Function MeasureTextLine(line As String, basePlane As Plane, height As Double, fontFace As String) As TextLineMetrics
        Dim result As New TextLineMetrics With {.Width = 0, .Height = height * 1.2R}
        If String.IsNullOrEmpty(line) Then Return result
        Using t As New Text3d(line, basePlane, height)
            ApplyFontToText3d(t, fontFace)
            t.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
            t.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
            Dim bb As BoundingBox = t.BoundingBox
            If Not bb.IsValid Then Return result
            Dim minX, maxX, minY, maxY As Double
            BoundingBoxPlaneExtents(bb, basePlane, minX, maxX, minY, maxY)
            result.Width = Math.Max(0, maxX - minX)
            result.Height = Math.Max(height * 1.2R, maxY - minY)
        End Using
        Return result
    End Function

    Private Shared Function HorizontalLineOffset(lineWidth As Double, blockWidth As Double, hAlign As Rhino.DocObjects.TextHorizontalAlignment) As Double
        Select Case hAlign
            Case Rhino.DocObjects.TextHorizontalAlignment.Right
                Return blockWidth - lineWidth
            Case Rhino.DocObjects.TextHorizontalAlignment.Center
                Return (blockWidth - lineWidth) * 0.5R
            Case Else
                Return 0
        End Select
    End Function

    Private Shared Function BlockAnchorLocalY(minY As Double, maxY As Double, vAlign As Rhino.DocObjects.TextVerticalAlignment) As Double
        Select Case vAlign
            Case Rhino.DocObjects.TextVerticalAlignment.Bottom
                Return minY
            Case Rhino.DocObjects.TextVerticalAlignment.Middle
                Return (minY + maxY) * 0.5R
            Case Else
                Return maxY
        End Select
    End Function

    Private Shared Function BlockAnchorLocalX(minX As Double, maxX As Double, hAlign As Rhino.DocObjects.TextHorizontalAlignment) As Double
        Select Case hAlign
            Case Rhino.DocObjects.TextHorizontalAlignment.Right
                Return maxX
            Case Rhino.DocObjects.TextHorizontalAlignment.Center
                Return (minX + maxX) * 0.5R
            Case Else
                Return minX
        End Select
    End Function

    Private Shared Function PlaneForBlockAnchor(pl As Plane, minX As Double, maxX As Double, minY As Double, maxY As Double,
                                                hAlign As Rhino.DocObjects.TextHorizontalAlignment,
                                                vAlign As Rhino.DocObjects.TextVerticalAlignment) As Plane
        Dim ax As Double = BlockAnchorLocalX(minX, maxX, hAlign)
        Dim ay As Double = BlockAnchorLocalY(minY, maxY, vAlign)
        Dim origin As Point3d = pl.Origin - pl.XAxis * ax - pl.YAxis * ay
        Return New Plane(origin, pl.XAxis, pl.YAxis)
    End Function

    Private Shared Function MeasureTextBlockExtents(txt As String, pl As Plane, height As Double, fontFace As String,
                                                    ByRef minX As Double, ByRef maxX As Double,
                                                    ByRef minY As Double, ByRef maxY As Double) As Boolean
        minX = 0 : maxX = 0 : minY = 0 : maxY = 0
        Using t As New Text3d(txt, pl, height)
            ApplyFontToText3d(t, fontFace)
            t.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
            t.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
            Dim bb As BoundingBox = t.BoundingBox
            If Not bb.IsValid Then Return False
            BoundingBoxPlaneExtents(bb, pl, minX, maxX, minY, maxY)
            Return True
        End Using
    End Function

    Private Sub DrawTagTextBlock(display As Rhino.Display.DisplayPipeline, txt As String, pl As Plane, col As Color,
                                 height As Double, fontFace As String,
                                 hAlign As Rhino.DocObjects.TextHorizontalAlignment,
                                 vAlign As Rhino.DocObjects.TextVerticalAlignment)
        Dim minX, maxX, minY, maxY As Double
        If Not MeasureTextBlockExtents(txt, pl, height, fontFace, minX, maxX, minY, maxY) Then Return
        Dim drawPl As Plane = PlaneForBlockAnchor(pl, minX, maxX, minY, maxY, hAlign, vAlign)
        Using t3 As New Text3d(txt, drawPl, height)
            ApplyFontToText3d(t3, fontFace)
            t3.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
            t3.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
            display.Draw3dText(t3, col)
        End Using
    End Sub

    Private Sub DrawTagText(display As Rhino.Display.DisplayPipeline, txt As String, pl As Plane, col As Color, index As Integer,
                            Optional heightScale As Double = 1.0R)
        If String.IsNullOrEmpty(txt) Then Return

        Dim height As Double = TextHeightForIndex(index) * Math.Max(0.01R, heightScale)
        Dim fontFace As String = FontFaceForIndex(index)
        Dim hAlign As Rhino.DocObjects.TextHorizontalAlignment = HorizontalAlignForIndex(index)
        Dim vAlign As Rhino.DocObjects.TextVerticalAlignment = VerticalAlignForIndex(index)
        Dim justifyLines As Boolean = JustifyMultilineForIndex(index)

        If Not justifyLines OrElse Not TextHasMultipleLines(txt) Then
            DrawTagTextBlock(display, txt, pl, col, height, fontFace, hAlign, vAlign)
            Return
        End If

        Dim lines As String() = SplitTextLines(txt)
        If lines.Length = 0 Then Return

        Dim minX, maxX, minY, maxY As Double
        If Not MeasureTextBlockExtents(txt, pl, height, fontFace, minX, maxX, minY, maxY) Then Return
        Dim anchorX As Double = BlockAnchorLocalX(minX, maxX, hAlign)
        Dim anchorY As Double = BlockAnchorLocalY(minY, maxY, vAlign)
        Dim blockWidth As Double = Math.Max(0, maxX - minX)

        Dim widths(lines.Length - 1) As Double
        Dim lineStep As Double = height * 1.2R
        For li As Integer = 0 To lines.Length - 1
            Dim m As TextLineMetrics = MeasureTextLine(lines(li), pl, height, fontFace)
            widths(li) = m.Width
            lineStep = Math.Max(lineStep, m.Height)
        Next

        For li As Integer = 0 To lines.Length - 1
            If String.IsNullOrEmpty(lines(li)) Then Continue For
            Dim lineX As Double = minX + HorizontalLineOffset(widths(li), blockWidth, hAlign)
            Dim lineY As Double = maxY - li * lineStep
            Dim lineOrigin As Point3d = pl.Origin + pl.XAxis * (lineX - anchorX) + pl.YAxis * (lineY - anchorY)
            Dim linePl As New Plane(lineOrigin, pl.XAxis, pl.YAxis)
            Using tLine As New Text3d(lines(li), linePl, height)
                ApplyFontToText3d(tLine, fontFace)
                tLine.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
                tLine.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
                display.Draw3dText(tLine, col)
            End Using
        Next
    End Sub

    Friend Function PlaneForTagViewport(index As Integer, vp As RhinoViewport) As Plane
        Dim s As TextTagSlot = Slots(index)
        If s.HasPlane Then Return s.Plane
        Return New Plane(s.Location, vp.CameraX, vp.CameraY)
    End Function

    Friend Function TryGetTagScreenRect(vp As RhinoViewport, index As Integer, ByRef screenRect As RectangleF) As Boolean
        screenRect = RectangleF.Empty
        If vp Is Nothing OrElse index < 0 OrElse index >= Slots.Count Then Return False
        Dim s As TextTagSlot = Slots(index)
        If Not s.Location.IsValid Then Return False
        Dim txt As String = If(index < Texts.Count, Texts(index), String.Empty)
        Const padPx As Single = 3.0F

        If String.IsNullOrEmpty(txt) Then
            If Not vp.IsVisible(s.Location) Then Return False
            Dim spt As Point2d = vp.WorldToClient(s.Location)
            Dim r As Single = CSng(TagPickRadiusPx)
            screenRect = New RectangleF(CSng(spt.X) - r, CSng(spt.Y) - r, r * 2.0F, r * 2.0F)
            Return True
        End If

        Dim pl As Plane = PlaneForTagViewport(index, vp)
        Dim height As Double = TextHeightForIndex(index)
        Dim fontFace As String = FontFaceForIndex(index)
        Dim minX, maxX, minY, maxY As Double
        If Not MeasureTextBlockExtents(txt, pl, height, fontFace, minX, maxX, minY, maxY) Then Return False

        Dim hAlign As Rhino.DocObjects.TextHorizontalAlignment = HorizontalAlignForIndex(index)
        Dim vAlign As Rhino.DocObjects.TextVerticalAlignment = VerticalAlignForIndex(index)
        Dim ax As Double = BlockAnchorLocalX(minX, maxX, hAlign)
        Dim ay As Double = BlockAnchorLocalY(minY, maxY, vAlign)

        Dim worldCorners() As Point3d = {
            pl.Origin + pl.XAxis * (minX - ax) + pl.YAxis * (minY - ay),
            pl.Origin + pl.XAxis * (maxX - ax) + pl.YAxis * (minY - ay),
            pl.Origin + pl.XAxis * (maxX - ax) + pl.YAxis * (maxY - ay),
            pl.Origin + pl.XAxis * (minX - ax) + pl.YAxis * (maxY - ay)
        }

        Dim minSX As Double = Double.PositiveInfinity
        Dim minSY As Double = Double.PositiveInfinity
        Dim maxSX As Double = Double.NegativeInfinity
        Dim maxSY As Double = Double.NegativeInfinity
        For Each wp As Point3d In worldCorners
            Dim sc As Point2d = vp.WorldToClient(wp)
            minSX = Math.Min(minSX, sc.X)
            minSY = Math.Min(minSY, sc.Y)
            maxSX = Math.Max(maxSX, sc.X)
            maxSY = Math.Max(maxSY, sc.Y)
        Next

        screenRect = New RectangleF(CSng(minSX) - padPx, CSng(minSY) - padPx,
                                    CSng(maxSX - minSX) + padPx * 2.0F, CSng(maxSY - minSY) + padPx * 2.0F)
        Return screenRect.Width > 0 AndAlso screenRect.Height > 0
    End Function

    Friend Function PickTagIndexAtViewport(vp As RhinoViewport, viewportPt As Drawing.Point) As Integer
        If vp Is Nothing Then Return -1
        Dim hit As Integer = -1
        Dim bestDist As Double = Double.PositiveInfinity
        For i As Integer = 0 To Slots.Count - 1
            If Not IsTagActiveForViewport(i) Then Continue For
            Dim rect As RectangleF
            If Not TryGetTagScreenRect(vp, i, rect) Then Continue For
            If Not rect.Contains(CSng(viewportPt.X), CSng(viewportPt.Y)) Then Continue For
            Dim cx As Double = CDbl(rect.X) + CDbl(rect.Width) * 0.5R
            Dim cy As Double = CDbl(rect.Y) + CDbl(rect.Height) * 0.5R
            Dim dx As Double = cx - CDbl(viewportPt.X)
            Dim dy As Double = cy - CDbl(viewportPt.Y)
            Dim d2 As Double = dx * dx + dy * dy
            If d2 < bestDist Then
                bestDist = d2
                hit = i
            End If
        Next
        Return hit
    End Function

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        If Slots.Count = 0 Then Return
        SyncMouse()

        For i As Integer = 0 To Slots.Count - 1
            Dim s As TextTagSlot = Slots(i)
            Dim txt As String = If(i < Texts.Count, Texts(i), String.Empty)

            Dim col As Color = TagColourForIndex(i)
            If Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected Then
                col = args.WireColour_Selected
            End If

            Dim isHover As Boolean = (i = HoverIndex)
            If isHover Then col = Color.Black

            If String.IsNullOrEmpty(txt) Then
                Dim ptSize As Integer = If(isHover, 8, 5)
                args.Display.DrawPoint(s.Location, PointStyle.RoundSimple, ptSize, col)
            Else
                Dim pl As Plane
                If s.HasPlane Then
                    pl = s.Plane
                Else
                    ' Camera-facing text for point input.
                    pl = New Plane(s.Location, args.Viewport.CameraX, args.Viewport.CameraY)
                End If
                DrawTagText(args.Display, txt, pl, col, i, If(isHover, HoverTextScale, 1.0R))
            End If
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim bb As BoundingBox = BoundingBox.Empty
            For Each s As TextTagSlot In Slots
                bb.Union(s.Location)
            Next
            Dim pad As Double = TextHeight
            If SlotSettings IsNot Nothing AndAlso SlotSettings.Length > 0 Then
                For si As Integer = 0 To SlotSettings.Length - 1
                    pad = Math.Max(pad, TextHeightForIndex(si))
                Next
            End If
            If bb.IsValid Then bb.Inflate(pad * 10.0R)
            Return bb
        End Get
    End Property

#End Region

#Region "Write/Read"

    Public Overrides Function Write(writer As GH_IO.Serialization.GH_IWriter) As Boolean
        writer.SetBoolean("TT_Preserve", PreserveChanges)
        writer.SetBoolean("TT_Proximity", ProximityCache)
        writer.SetBoolean("TT_SaveShifted", ProximityCache)
        writer.SetInt32("TT_HAlign", CInt(HorizontalAlign))
        writer.SetInt32("TT_VAlign", CInt(VerticalAlign))
        writer.SetBoolean("TT_JustifyLines", JustifyMultilineLines)
        writer.SetBoolean("TT_LockUnselected", LockUnselected)
        writer.SetInt32("TT_Count", Texts.Count)
        For i As Integer = 0 To Texts.Count - 1
            writer.SetString("TT_Text", i, If(Texts(i), String.Empty))
        Next
        writer.SetInt32("TT_EditedCount", TextUserEdited.Count)
        For i As Integer = 0 To TextUserEdited.Count - 1
            writer.SetBoolean("TT_Edited", i, TextUserEdited(i))
        Next
        writer.SetInt32("TT_ShiftedCount", ShiftedTextEntries.Count)
        For i As Integer = 0 To ShiftedTextEntries.Count - 1
            Dim entry As ShiftedTextEntry = ShiftedTextEntries(i)
            writer.SetDouble("TT_ShiftCx", i, entry.Key.Location.X)
            writer.SetDouble("TT_ShiftCy", i, entry.Key.Location.Y)
            writer.SetDouble("TT_ShiftCz", i, entry.Key.Location.Z)
            writer.SetBoolean("TT_ShiftPlane", i, entry.Key.HasPlane)
            writer.SetDouble("TT_ShiftPz", i, entry.Key.PlaneZx)
            writer.SetDouble("TT_ShiftPy", i, entry.Key.PlaneZy)
            writer.SetDouble("TT_ShiftPzz", i, entry.Key.PlaneZz)
            writer.SetString("TT_ShiftText", i, If(entry.Text, String.Empty))
            writer.SetBoolean("TT_ShiftEdited", i, entry.UserEdited)
        Next
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(reader As GH_IO.Serialization.GH_IReader) As Boolean
        Dim preserve As Boolean = True
        reader.TryGetBoolean("TT_Preserve", preserve)
        PreserveChanges = preserve

        Dim prox As Boolean = False
        reader.TryGetBoolean("TT_Proximity", prox)
        ProximityCache = prox
        ' Save-shifted is always on with proximity; legacy TT_SaveShifted is ignored.
        SaveShifted = prox
        Dim discardSs As Boolean = False
        reader.TryGetBoolean("TT_SaveShifted", discardSs)

        Dim hAlign As Integer = CInt(Rhino.DocObjects.TextHorizontalAlignment.Center)
        If reader.TryGetInt32("TT_HAlign", hAlign) Then
            HorizontalAlign = CType(hAlign, Rhino.DocObjects.TextHorizontalAlignment)
        End If

        Dim vAlign As Integer = CInt(Rhino.DocObjects.TextVerticalAlignment.Middle)
        If reader.TryGetInt32("TT_VAlign", vAlign) Then
            VerticalAlign = CType(vAlign, Rhino.DocObjects.TextVerticalAlignment)
        End If

        Dim justifyLines As Boolean = False
        reader.TryGetBoolean("TT_JustifyLines", justifyLines)
        JustifyMultilineLines = justifyLines

        Dim lockUnsel As Boolean = True
        reader.TryGetBoolean("TT_LockUnselected", lockUnsel)
        LockUnselected = lockUnsel

        Texts.Clear()
        TextUserEdited.Clear()
        Dim n As Integer = 0
        If reader.TryGetInt32("TT_Count", n) Then
            For i As Integer = 0 To n - 1
                Dim s As String = String.Empty
                reader.TryGetString("TT_Text", i, s)
                Texts.Add(If(s, String.Empty))
            Next
        End If

        Dim nEdited As Integer = 0
        If reader.TryGetInt32("TT_EditedCount", nEdited) Then
            For i As Integer = 0 To nEdited - 1
                Dim edited As Boolean = False
                reader.TryGetBoolean("TT_Edited", i, edited)
                TextUserEdited.Add(edited)
            Next
        Else
            For Each s As String In Texts
                TextUserEdited.Add(Not String.IsNullOrEmpty(s))
            Next
        End If

        ShiftedTextEntries.Clear()
        Dim shiftedCount As Integer = 0
        If reader.TryGetInt32("TT_ShiftedCount", shiftedCount) AndAlso shiftedCount > 0 Then
            For i As Integer = 0 To shiftedCount - 1
                Dim entry As New ShiftedTextEntry
                Dim cx As Double = 0, cy As Double = 0, cz As Double = 0
                reader.TryGetDouble("TT_ShiftCx", i, cx)
                reader.TryGetDouble("TT_ShiftCy", i, cy)
                reader.TryGetDouble("TT_ShiftCz", i, cz)
                entry.Key.Location = New Point3d(cx, cy, cz)
                reader.TryGetBoolean("TT_ShiftPlane", i, entry.Key.HasPlane)
                reader.TryGetDouble("TT_ShiftPz", i, entry.Key.PlaneZx)
                reader.TryGetDouble("TT_ShiftPy", i, entry.Key.PlaneZy)
                reader.TryGetDouble("TT_ShiftPzz", i, entry.Key.PlaneZz)
                Dim txt As String = String.Empty
                reader.TryGetString("TT_ShiftText", i, txt)
                entry.Text = If(txt, String.Empty)
                reader.TryGetBoolean("TT_ShiftEdited", i, entry.UserEdited)
                If entry.Key.Location.IsValid Then ShiftedTextEntries.Add(entry)
            Next
        End If
        SyncOptionalInputsFromFlags()
        Return MyBase.Read(reader)
    End Function

#End Region

End Class

Public Class TextTagCompAtt
    Inherits Grasshopper.Kernel.Attributes.GH_ComponentAttributes

    Private MyOwner As TextTagComp

    Sub New(owner As TextTagComp)
        MyBase.New(owner)
        MyOwner = owner
    End Sub

    Public Overrides Property Selected As Boolean
        Get
            Return MyBase.Selected
        End Get
        Set(value As Boolean)
            MyBase.Selected = value
            MyOwner.SyncMouse()
        End Set
    End Property

End Class

''' <summary>In-session undo for entered texts / preserve flag.</summary>
Public Class TextTagUndo
    Inherits Grasshopper.Kernel.Undo.GH_UndoAction

    Private ReadOnly _ownerId As Guid
    Private _texts As List(Of String)
    Private _edited As List(Of Boolean)
    Private _preserve As Boolean
    Private _proximity As Boolean
    Private _saveShifted As Boolean
    Private _shifted As List(Of ShiftedTextEntry)
    Private _hAlign As Rhino.DocObjects.TextHorizontalAlignment
    Private _vAlign As Rhino.DocObjects.TextVerticalAlignment
    Private _justifyLines As Boolean
    Private _lockUnselected As Boolean

    Sub New(owner As TextTagComp)
        _ownerId = owner.InstanceGuid
        _texts = New List(Of String)(owner.Texts)
        _edited = New List(Of Boolean)(owner.TextUserEdited)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _saveShifted = owner.SaveShifted
        _shifted = CloneShiftedTextListForUndo(owner.ShiftedTextEntries)
        _hAlign = owner.HorizontalAlign
        _vAlign = owner.VerticalAlign
        _justifyLines = owner.JustifyMultilineLines
        _lockUnselected = owner.LockUnselected
    End Sub

    Private Shared Function CloneShiftedTextListForUndo(src As List(Of ShiftedTextEntry)) As List(Of ShiftedTextEntry)
        If src Is Nothing Then Return New List(Of ShiftedTextEntry)
        Return New List(Of ShiftedTextEntry)(src)
    End Function

    Protected Overrides Sub Internal_Undo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Protected Overrides Sub Internal_Redo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Private Sub SwapState(doc As GH_Document)
        Dim comp As TextTagComp = TryCast(doc.FindObject(_ownerId, True), TextTagComp)
        If comp Is Nothing Then Return
        Dim curTexts As New List(Of String)(comp.Texts)
        Dim curEdited As New List(Of Boolean)(comp.TextUserEdited)
        Dim curPreserve As Boolean = comp.PreserveChanges
        Dim curProximity As Boolean = comp.ProximityCache
        Dim curSaveShifted As Boolean = comp.SaveShifted
        Dim curShifted As List(Of ShiftedTextEntry) = CloneShiftedTextListForUndo(comp.ShiftedTextEntries)
        Dim curH As Rhino.DocObjects.TextHorizontalAlignment = comp.HorizontalAlign
        Dim curV As Rhino.DocObjects.TextVerticalAlignment = comp.VerticalAlign
        Dim curJustify As Boolean = comp.JustifyMultilineLines
        Dim curLockUnselected As Boolean = comp.LockUnselected
        comp.SetTagTextsFromUndo(_texts, _edited, _preserve, _proximity, _saveShifted, _shifted, _hAlign, _vAlign, _justifyLines, _lockUnselected)
        _texts = curTexts
        _edited = curEdited
        _preserve = curPreserve
        _proximity = curProximity
        _saveShifted = curSaveShifted
        _shifted = curShifted
        _hAlign = curH
        _vAlign = curV
        _justifyLines = curJustify
        _lockUnselected = curLockUnselected
    End Sub

End Class

''' <summary>Viewport clicks on the tag dot/text (enabled only while the component is selected on canvas).</summary>
Public Class TextTagMouse
    Inherits Rhino.UI.MouseCallback

    Private ReadOnly Comp As TextTagComp

    Sub New(owner As TextTagComp)
        Comp = owner
    End Sub

    ''' <summary>Beyond this many pixels the gesture counts as a drag (e.g. moving the underlying point), not a click.</summary>
    Private Const ClickSlopPx As Double = 4.0R

    Private _pendingHit As Integer = -1
    Private _downViewport As Drawing.Point
    Private _hoverTimer As Timer
    Private _hoverPollActive As Boolean
    Private _hookedCanvas As GH_Canvas

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
        If Not _hoverPollActive OrElse Comp Is Nothing OrElse _pendingHit >= 0 OrElse Comp.TagTextBox IsNot Nothing Then Return
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
                Comp.SetHoverIndex(Comp.PickTagIndexAtViewport(vp, clientPt))
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
        Comp.SetHoverIndex(Comp.PickTagIndexAtViewport(vp, e.ViewportPoint))
    End Sub

    Private Sub ResumeHoverPoll()
        If _hoverPollActive AndAlso _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = True
        PollHoverFromGlobalCursor()
    End Sub

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        _pendingHit = -1
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If e.View Is Nothing Then Exit Sub

        ' A fresh click always retires any open floating box first.
        Comp.CloseTagTextBoxIfAny()

        Dim vp As RhinoViewport = e.View.ActiveViewport
        If vp Is Nothing Then Exit Sub

        Dim hit As Integer = Comp.PickTagIndexAtViewport(vp, e.ViewportPoint)

        If hit < 0 Then Exit Sub

        ' Swallow the press so Rhino does not start a crossing/window selection rectangle.
        ' Drag past the click slop clears the pending edit so the move can reach Rhino (e.g. point drag).
        _pendingHit = hit
        _downViewport = e.ViewportPoint
        If _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = False
        Comp.SetHoverIndex(hit)
        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)
        If _pendingHit < 0 Then
            UpdateHoverFromViewport(e)
            Exit Sub
        End If
        Dim dx As Double = CDbl(e.ViewportPoint.X) - CDbl(_downViewport.X)
        Dim dy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_downViewport.Y)
        If (dx * dx + dy * dy) > (ClickSlopPx * ClickSlopPx) Then
            _pendingHit = -1
            ResumeHoverPoll()
            Exit Sub
        End If
        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseUp(e)
        Dim hit As Integer = _pendingHit
        _pendingHit = -1
        If hit < 0 Then
            ResumeHoverPoll()
            Exit Sub
        End If
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If hit >= Comp.Slots.Count Then Exit Sub

        Comp.EditIndex = hit
        Dim current As String = If(hit < Comp.Texts.Count, Comp.Texts(hit), String.Empty)
        Comp.TagTextBox = New FormTextTagBox(Control.MousePosition, Comp, hit, current)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
        ResumeHoverPoll()
        e.Cancel = True
    End Sub

End Class

''' <summary>Floating multiline text entry for a tag; Enter commits, Shift+Enter adds a line, Escape / click elsewhere dismisses.</summary>
Public Class FormTextTagBox
    Inherits System.Windows.Forms.Form

    Private Shared _activeInstance As FormTextTagBox

    ''' <summary>Rhino MouseCallback route: dismiss when the viewport is pressed while this float has focus.</summary>
    Friend Shared Function ConsumeBackdropMouseDown() As Boolean
        Dim f As FormTextTagBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed OrElse Not f.Visible Then Return False
        If f._committing OrElse Not f._outsideDismissReady Then Return False
        If Environment.TickCount < f._suppressBackdropDismissUntil Then Return False
        f.TryDismissFromOutsideRhinoGesture()
        Return True
    End Function

    Friend Shared Sub RequestDismissFromBackdropMouse()
        ConsumeBackdropMouseDown()
    End Sub

    Private Comp As TextTagComp
    Private ReadOnly SlotIndex As Integer
    Private _committing As Boolean
    Private _outsideDismissReady As Boolean
    Private _suppressBackdropDismissUntil As Integer
    Private _hookedCanvas As GH_Canvas

    Sub New(screenLocation As Drawing.Point, owner As TextTagComp, index As Integer, initialText As String)
        ClosePriorFloatingLeakNoCancelPending()
        Comp = owner
        SlotIndex = index
        InitializeComponent()
        Me.StartPosition = FormStartPosition.Manual
        Dim padX As Integer = 10
        Dim padY As Integer = -24
        Dim loc As New Drawing.Point(screenLocation.X + padX, screenLocation.Y + padY)
        Dim wa As Drawing.Rectangle = System.Windows.Forms.Screen.GetWorkingArea(loc)
        If loc.X < wa.Left Then loc.X = wa.Left
        If loc.Y < wa.Top Then loc.Y = wa.Top
        If loc.X + Me.Width > wa.Right Then loc.X = Math.Max(wa.Left, wa.Right - Me.Width)
        If loc.Y + Me.Height > wa.Bottom Then loc.Y = Math.Max(wa.Top, wa.Bottom - Me.Height)
        Me.Location = loc
        TextBox1.Text = If(initialText, String.Empty)
        TextBox1.SelectAll()
        _suppressBackdropDismissUntil = Environment.TickCount + 420
        _activeInstance = Me
        GumballNumericBackdropMouse.Instance.EnsureEnabled()
        Me.Show()
    End Sub

    Private Shared Sub ClosePriorFloatingLeakNoCancelPending()
        If _activeInstance Is Nothing OrElse _activeInstance.IsDisposed Then Return
        Dim p As FormTextTagBox = _activeInstance
        _activeInstance = Nothing
        p.SilentCloseLeakWithoutCancelPending()
    End Sub

    Private Sub SilentCloseLeakWithoutCancelPending()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If Comp IsNot Nothing Then Comp.ForgetFloatingTagTextBox()
        Comp = Nothing
        Try
            Close()
        Catch
        End Try
    End Sub

    Private Sub TryDismissFromOutsideRhinoGesture()
        If _committing OrElse Not _outsideDismissReady Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        If Not Visible Then Return
        Dim self As FormTextTagBox = Me
        BeginInvoke(New Action(Sub()
                                   If self._committing Then Return
                                   If Not self.Visible Then Return
                                   self.DismissWithoutCommit()
                               End Sub))
    End Sub

    Private Sub Canvas_MouseDownDismissHook(sender As Object, e As MouseEventArgs)
        If _committing OrElse Not _outsideDismissReady Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        If e.Button <> MouseButtons.Left Then Return
        Dim cv As GH_Canvas = TryCast(sender, GH_Canvas)
        If cv Is Nothing Then Return
        Dim screenPt As Drawing.Point = cv.PointToScreen(e.Location)
        If Me.Bounds.Contains(screenPt) Then Return
        TryDismissFromOutsideRhinoGesture()
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

    Private Sub AttachGrasshopperCanvasDismissHookInternal()
        DetachGrasshopperCanvasDismissHookInternal()
        Dim cv As GH_Canvas = TryResolveGrasshopperCanvas()
        If cv Is Nothing Then Return
        _hookedCanvas = cv
        AddHandler _hookedCanvas.MouseDown, AddressOf Canvas_MouseDownDismissHook
    End Sub

    Private Sub DetachGrasshopperCanvasDismissHookInternal()
        If _hookedCanvas Is Nothing Then Return
        RemoveHandler _hookedCanvas.MouseDown, AddressOf Canvas_MouseDownDismissHook
        _hookedCanvas = Nothing
    End Sub

    Private Shared Sub RefreshBackdropMouseCallbackListening()
        ' Floats dismiss on Deactivate, so at most one is alive at a time; safe to stop the shared backdrop callback.
        If _activeInstance Is Nothing OrElse _activeInstance.IsDisposed Then
            GumballNumericBackdropMouse.Instance.Enabled = False
        End If
    End Sub

    ''' <summary>Cancel text edit (Escape, click outside, lost activation).</summary>
    Friend Sub DismissWithoutCommit()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If Comp IsNot Nothing Then Comp.CancelPendingTextInput()
        Close()
    End Sub

    Private Sub FormTextTagBox_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        TextBox1.Focus()
        GumballNumericBackdropMouse.Instance.EnsureEnabled()
        AttachGrasshopperCanvasDismissHookInternal()
        Dim arm As New Timer With {.Interval = 180}
        AddHandler arm.Tick,
            Sub()
                arm.Stop()
                arm.Dispose()
                _outsideDismissReady = True
            End Sub
        arm.Start()
    End Sub

    Private Sub TextBox1_LostFocus(sender As Object, e As EventArgs) Handles TextBox1.LostFocus
        If Not _outsideDismissReady OrElse _committing Then Return
        BeginInvoke(Sub()
                        If _committing Then Return
                        If Not Me.Visible Then Return
                        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
                        DismissWithoutCommit()
                    End Sub)
    End Sub

    Private Sub FormTextTagBox_Deactivate(sender As Object, e As EventArgs) Handles MyBase.Deactivate
        If Not _outsideDismissReady OrElse _committing Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        DismissWithoutCommit()
    End Sub

    Private Sub TryCommitEntry()
        If _committing OrElse Comp Is Nothing OrElse TextBox1 Is Nothing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        Try
            Comp.CommitTagText(SlotIndex, TextBox1.Text)
        Finally
            Close()
        End Try
    End Sub

    Private Sub TextBox1_KeyDown(sender As Object, e As KeyEventArgs) Handles TextBox1.KeyDown
        If e.KeyCode = Keys.Escape Then
            e.SuppressKeyPress = True
            DismissWithoutCommit()
            Return
        End If
        If e.KeyCode = Keys.Enter OrElse e.KeyCode = Keys.Return Then
            If e.Shift Then
                ' Shift+Enter inserts a newline in the multiline box.
                Return
            End If
            e.SuppressKeyPress = True
            TryCommitEntry()
        End If
    End Sub

    Private Sub TextBox1_KeyPress(sender As Object, e As KeyPressEventArgs) Handles TextBox1.KeyPress
        If e.KeyChar = ChrW(13) OrElse e.KeyChar = ChrW(10) Then
            If (Control.ModifierKeys And Keys.Shift) = Keys.Shift Then
                Return
            End If
            e.Handled = True
            TryCommitEntry()
        End If
    End Sub

    Private Sub FormTextTagBox_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        DetachGrasshopperCanvasDismissHookInternal()
    End Sub

    Private Sub FormTextTagBox_FormClosed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If ReferenceEquals(_activeInstance, Me) Then
            _activeInstance = Nothing
        End If
        RefreshBackdropMouseCallbackListening()
        If Comp IsNot Nothing Then Comp.ForgetFloatingTagTextBox()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing Then
                DetachGrasshopperCanvasDismissHookInternal()
            End If
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    Private components As System.ComponentModel.IContainer

    Private Sub InitializeComponent()
        Me.TextBox1 = New System.Windows.Forms.TextBox()
        Me.SuspendLayout()
        '
        'TextBox1
        '
        Me.TextBox1.Dock = System.Windows.Forms.DockStyle.Fill
        Me.TextBox1.Location = New System.Drawing.Point(0, 0)
        Me.TextBox1.Name = "TextBox1"
        Me.TextBox1.Size = New System.Drawing.Size(200, 72)
        Me.TextBox1.TabIndex = 0
        Me.TextBox1.Multiline = True
        Me.TextBox1.AcceptsReturn = True
        Me.TextBox1.ScrollBars = System.Windows.Forms.ScrollBars.Vertical
        Me.TextBox1.WordWrap = True
        '
        'FormTextTagBox
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(200, 72)
        Me.Controls.Add(Me.TextBox1)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
        Me.StartPosition = FormStartPosition.Manual
        Me.MaximumSize = New System.Drawing.Size(200, 120)
        Me.MinimumSize = New System.Drawing.Size(200, 72)
        Me.Name = "FormTextTagBox"
        Me.Text = "TextTag"
        Me.Owner = Grasshopper.Instances.DocumentEditor
        Me.ResumeLayout(False)
        Me.PerformLayout()
    End Sub

    Friend WithEvents TextBox1 As TextBox
End Class
