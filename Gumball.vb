Imports System.Collections.Generic
Imports System.Collections
Imports System.IO
Imports System.Linq
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Reflection
Imports System.Windows.Forms
Imports Grasshopper
Imports Grasshopper.Kernel
Imports Grasshopper.GUI
Imports GH_IO.Serialization
Imports Rhino.Display
Imports Rhino.Geometry
Imports Grasshopper.Kernel.Data
Imports GH_IO
Imports Grasshopper.Kernel.Types
Imports Grasshopper.GUI.Canvas

''' <summary>Receives Rhino viewport mouse downs even when a WinForms float steals focus, so we can dismiss the numeric box.</summary>
Friend NotInheritable Class GumballNumericBackdropMouse
    Inherits Rhino.UI.MouseCallback

    Friend Shared ReadOnly Instance As New GumballNumericBackdropMouse()

    Private Sub New()
    End Sub

    Friend Sub EnsureEnabled()
        If Not Me.Enabled Then Me.Enabled = True
    End Sub

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        If FormTextBox.ConsumeBackdropMouseDown() OrElse
            FormAttributes.ConsumeBackdropMouseDown() OrElse
            FormTextTagBox.ConsumeBackdropMouseDown() OrElse
            FormCurveSliderBox.ConsumeBackdropMouseDown() Then
            e.Cancel = True
        End If
    End Sub
End Class

Public Class GumballComp
    Inherits GH_Component
    Implements IGH_VariableParameterComponent

    Private Enum ZuiOptionalKind
        None = -1
        Active = 0
        ApplyToAll = 1
        DisplayMode = 2
        AlignToGeometry = 3
        SnapToGeometry = 4
        PreserveChanges = 5
        ProximityCache = 6
        LiveTransforms = 7
        ClearCache = 8
        SaveShifted = 9
        RelocateGumball = 10
    End Enum

    Private Shared ReadOnly ZuiCanonicalOrder As ZuiOptionalKind() = {
        ZuiOptionalKind.Active,
        ZuiOptionalKind.ApplyToAll,
        ZuiOptionalKind.DisplayMode,
        ZuiOptionalKind.AlignToGeometry,
        ZuiOptionalKind.SnapToGeometry,
        ZuiOptionalKind.RelocateGumball,
        ZuiOptionalKind.PreserveChanges,
        ZuiOptionalKind.ProximityCache,
        ZuiOptionalKind.LiveTransforms,
        ZuiOptionalKind.ClearCache
    }

    Private Const BaseInputCount As Integer = 1

    ''' <summary>Per gumball-slot settings resolved from optional tree inputs (matched to geometry paths).</summary>
    Friend Structure GumballSlotSettings
        Public Active As Boolean
        Public ApplyToAll As Boolean
        Public DisplayMode As Integer
        ''' <summary>When true, grip transforms relocate the gumball frame only (geometry unchanged).</summary>
        Public Relocate As Boolean
    End Structure

    Friend SlotSettings As GumballSlotSettings()

    Private _clearCacheInputPrev As Boolean = False

    Public Sub New()
        MyBase.New("Gumball ", "Gumball", "Viewport gumball: Shift while scaling = uniform XYZ (like Rhino); click a grip (no drag) for numeric entry.", "Params", "Util")
    End Sub

#Region "Overrides"
    Private Shared ReadOnly GumballIconResourceName As String = "GumballGH.GumballIcon.png"

    Private Shared _gumballIcon As System.Drawing.Bitmap

    Private Shared Function BuildFallbackGumballIcon24x24() As System.Drawing.Bitmap
        Const w As Integer = 24, h As Integer = 24
        Dim bmp As New System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb)
        Dim bg As System.Drawing.Color = System.Drawing.Color.FromArgb(255, 230, 110, 55)
        Dim axis As System.Drawing.Color = System.Drawing.Color.FromArgb(255, 40, 40, 40)
        Dim cx As Integer = w \ 2, cy As Integer = h \ 2
        For yy As Integer = 0 To h - 1
            For xx As Integer = 0 To w - 1
                Dim c As System.Drawing.Color = bg
                If (Math.Abs(xx - cx) <= 9 AndAlso Math.Abs(yy - cy) <= 1) OrElse
                        (Math.Abs(yy - cy) <= 9 AndAlso Math.Abs(xx - cx) <= 1) Then
                    c = axis
                End If
                bmp.SetPixel(xx, yy, c)
            Next
        Next
        Return bmp
    End Function

    ''' <summary>Embedded PNG (see project EmbeddedResource); scaled to 24×24 for Grasshopper.</summary>
    Private Shared Function LoadEmbeddedGumballIcon() As System.Drawing.Bitmap
        Const target As Integer = 24
        Try
            Dim asm As Assembly = Assembly.GetExecutingAssembly()
            Using src As Stream = asm.GetManifestResourceStream(GumballIconResourceName)
                If src Is Nothing Then Return BuildFallbackGumballIcon24x24()
                Using srcImg As Image = Image.FromStream(src)
                    Dim bmp As New Bitmap(target, target, PixelFormat.Format32bppArgb)
                    Using g As Graphics = Graphics.FromImage(bmp)
                        g.Clear(Color.Transparent)
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality
                        g.DrawImage(srcImg, New Rectangle(0, 0, target, target))
                    End Using
                    Return bmp
                End Using
            End Using
        Catch
            Return BuildFallbackGumballIcon24x24()
        End Try
    End Function

    Protected Overrides ReadOnly Property Icon() As System.Drawing.Bitmap
        Get
            If (_gumballIcon Is Nothing) Then _gumballIcon = LoadEmbeddedGumballIcon()
            Return _gumballIcon
        End Get
    End Property

    Public Overrides ReadOnly Property ComponentGuid() As Guid
        Get
            Return New Guid("{7b5a45b5-5ecc-4e34-9dcf-bfdeb8cc8deb}")
        End Get
    End Property

    Protected Overrides Sub RegisterInputParams(pManager As GH_Component.GH_InputParamManager)
        pManager.AddGeometryParameter("Geometry", "G", "Geometry to add a gumball", GH_ParamAccess.tree)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddGeometryParameter("Geometry", "G", "Transformed geometry", GH_ParamAccess.tree)
        pManager.AddTransformParameter("Transform", "X", "Transformation data", GH_ParamAccess.tree)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New GumballCompAtt(Me)
    End Sub

    Public Overrides Sub AddedToDocument(document As GH_Document)
        MyBase.AddedToDocument(document)
        ViewportPreview.EnsureGrasshopperDocumentHooks(document)
        SyncOptionalInputsFromFlags()
    End Sub

    Public Overrides Sub RemovedFromDocument(document As GH_Document)
        If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
        MyGumball = Nothing
        _previewRhinoDoc = Nothing
    End Sub

    Public Overrides Sub MovedBetweenDocuments(oldDocument As GH_Document, newDocument As GH_Document)
        If newDocument IsNot Nothing Then ViewportPreview.EnsureGrasshopperDocumentHooks(newDocument)
        If (MyGumball IsNot Nothing) Then MyGumball.HideGumballs()
        SyncGumballVisibility()
    End Sub

    Public Overrides Sub DocumentContextChanged(document As GH_Document, context As GH_DocumentContext)
        MyBase.DocumentContextChanged(document, context)
        If (context = GH_DocumentContext.Close) AndAlso (MyGumball IsNot Nothing) Then MyGumball.Dispose()
    End Sub

    Public Overrides Property Locked As Boolean
        Get
            Return MyBase.Locked
        End Get
        Set(value As Boolean)
            If (MyGumball IsNot Nothing) AndAlso (value) Then MyGumball.HideGumballs()
            MyBase.Locked = value
        End Set
    End Property

    Protected Overrides Sub AfterSolveInstance()
        SyncGumballVisibility()
    End Sub

    Protected Overrides Sub AppendAdditionalComponentMenuItems(ByVal menu As Windows.Forms.ToolStripDropDown)

        Dim union As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Apply to all", AddressOf Me.Menu_ApplyToAll, True, MenuBoolChecked(ModeValueType = 1, ZuiOptionalKind.ApplyToAll))
        union.ToolTipText = "Performs transformation of a gumball to all geometry"

        Dim aling As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align to geometry", AddressOf Me.Menu_AlingToGeometry, True, HasZuiInput(ZuiOptionalKind.AlignToGeometry) OrElse CBool(Me.ModeValue(2)))
        aling.ToolTipText = "Use a geometry to align gumballs"

        Dim snapGeom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Snap to geometry", AddressOf Me.Menu_SnapToGeometry, True, HasZuiInput(ZuiOptionalKind.SnapToGeometry) OrElse CBool(Me.ModeValue(3)))
        snapGeom.ToolTipText = "Shows inputs S (targets) and t (max snap distance, doc units, optional): while translating grips, snaps toward the nearest point within that distance."

        Dim reloc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Relocate gumball", AddressOf Me.Menu_RelocateG, True, HasZuiInput(ZuiOptionalKind.RelocateGumball) OrElse Me.ModeValue(0) = 2)
        reloc.ToolTipText = "Relocate gumball without affecting the geometry. Optional input R (tree matches G): point or plane per item."

        Dim reset As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Reset gumball", AddressOf Me.Menu_Reset, True)
        reset.ToolTipText = "Restore gumball to world coordinates"

        Dim CC As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Me.Menu_ClearCache, True)
        CC.ToolTipText = "Reset gumball and clear cache data"

        Menu_AppendSeparator(menu)

        Dim listCache As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "List cache", AddressOf Me.Menu_PreserveOnChanges, True, MenuBoolChecked(PreserveTransformsOnGeometryChange, ZuiOptionalKind.PreserveChanges))
        listCache.ToolTipText = "Keep gumball transforms by tree path / list index when geometry moves. With Proximity also on: keep by index for far moves; proximity remaps wrap-shifts, culls, grafts, and tree changes."

        Dim proximityItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Me.Menu_ProximityCache, True, MenuBoolChecked(ProximityCache, ZuiOptionalKind.ProximityCache))
        proximityItem.ToolTipText = "Re-attach transforms by nearest cached bounding-box centre on wrap-shifts, culls, grafts, and other list/tree changes. Culled geometry is always saved and restored if it returns (save-shifted)."

        Dim liveItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Live", AddressOf Me.Menu_LiveTransformsWhileDragging, True, MenuBoolChecked(LiveTransformsWhileDragging, ZuiOptionalKind.LiveTransforms))
        liveItem.ToolTipText = "Refresh downstream Grasshopper while dragging the gumball; one undo compound entry per finished drag."

        Menu_AppendSeparator(menu)

        Dim displayMode As Integer = EffectiveDisplayModeForMenu()

        Dim arrows As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Only arrows", AddressOf Me.Menu_OnlyArrows, True, displayMode = 1)
        arrows.ToolTipText = "Show only translation arrows (hide planar grips and scale)."

        Dim rotOnly As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Only rotation", AddressOf Me.Menu_OnlyRotation, True, displayMode = 2)
        rotOnly.ToolTipText = "Show only rotation arcs (hide translation and scale grips)."

        Dim scaleOnly As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Only scale", AddressOf Me.Menu_OnlyScale, True, displayMode = 3)
        scaleOnly.ToolTipText = "Show only scale grips (hide translation and rotation grips)."

        Dim free As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Free translate", AddressOf Me.Menu_FreeTranslate, True, displayMode = 4)
        free.ToolTipText = "Hide all grips and drag from the gumball center."

        Menu_AppendSeparator(menu)

        Dim Rad As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Attributes", AddressOf Me.Menu_Attributes, True)
        Rad.ToolTipText = "Changes the gumball attributes"

    End Sub

#End Region

#Region "Menu"
    Private Sub Menu_ApplyToAll()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        Dim enable As Boolean = (ModeValueType <> 1)
        ModeValue(0) = If(enable, 1, 0)
    End Sub

    Private Sub Menu_AlingToGeometry()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        SetZuiKindEnabled(ZuiOptionalKind.AlignToGeometry, Not (HasZuiInput(ZuiOptionalKind.AlignToGeometry) OrElse ModeValueAlign))
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_SnapToGeometry()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        SetZuiKindEnabled(ZuiOptionalKind.SnapToGeometry, Not (HasZuiInput(ZuiOptionalKind.SnapToGeometry) OrElse ModeValueSnap))
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_RelocateG()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        If HasZuiInput(ZuiOptionalKind.RelocateGumball) Then
            SetZuiKindEnabled(ZuiOptionalKind.RelocateGumball, False)
            Me.ExpireSolution(True)
            Return
        End If
        If (2 = Me.ModeValue(0)) Then
            Me.ModeValue(0) = 0
        Else
            Me.ModeValue(0) = 2
        End If
    End Sub

    Private Sub Menu_Attributes()
        Try
            If AttForm IsNot Nothing AndAlso Not AttForm.IsDisposed Then
                AttForm.RequestDismiss()
                Exit Sub
            End If
            AttForm = New FormAttributes(Me)
        Catch ex As Exception
            AttForm = Nothing
            Rhino.RhinoApp.WriteLine("GumballGH: could not open Attributes — " & ex.Message)
        End Try
    End Sub

    Private Sub SetDisplayModeFromMenu(mode As Integer)
        If MyGumball Is Nothing Then Exit Sub
        Me.RecordUndoEvent("Gumball Attributes", New GbUndo(Me.MyGumball))
        If ModeValueAtt = mode Then
            ApplyModeValueAtt(0)
        Else
            ApplyModeValueAtt(mode)
        End If
    End Sub

    Private Sub Menu_OnlyArrows()
        SetDisplayModeFromMenu(1)
    End Sub

    Private Sub Menu_FreeTranslate()
        SetDisplayModeFromMenu(4)
    End Sub

    Private Sub Menu_OnlyRotation()
        SetDisplayModeFromMenu(2)
    End Sub

    Private Sub Menu_OnlyScale()
        SetDisplayModeFromMenu(3)
    End Sub

    Private Sub Menu_Reset()
        Me.RecordUndoEvent("Gumball Reset")
        If (MyGumball IsNot Nothing) Then MyGumball.RestoreGumball()
    End Sub

    Public Sub Menu_ClearCache()
        Me.RecordUndoEvent("Gumball Clear Cache")
        ClearGumballCacheInternal()
    End Sub

    Private Sub Menu_ProximityCache()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Proximity cache", New GbUndo(Me.MyGumball))
        ProximityCache = Not ProximityCache
        If ProximityCache Then SaveShifted = True
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_PreserveOnChanges()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("List cache", New GbUndo(Me.MyGumball))
        PreserveTransformsOnGeometryChange = Not PreserveTransformsOnGeometryChange
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_LiveTransformsWhileDragging()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Live (while dragging)", New GbUndo(Me.MyGumball))
        LiveTransformsWhileDragging = Not LiveTransformsWhileDragging
        Me.ExpireSolution(True)
    End Sub
#End Region

#Region "Optional inputs / ZUI"

    Private Shared Function NickNameForZuiKind(kind As ZuiOptionalKind) As String
        Select Case kind
            Case ZuiOptionalKind.Active : Return "Ac"
            Case ZuiOptionalKind.ApplyToAll : Return "Aa"
            Case ZuiOptionalKind.DisplayMode : Return "Dm"
            Case ZuiOptionalKind.AlignToGeometry : Return "A"
            Case ZuiOptionalKind.SnapToGeometry : Return "S"
            Case ZuiOptionalKind.RelocateGumball : Return "R"
            Case ZuiOptionalKind.PreserveChanges : Return "Pr"
            Case ZuiOptionalKind.ProximityCache : Return "Px"
            Case ZuiOptionalKind.SaveShifted : Return "Ss"
            Case ZuiOptionalKind.LiveTransforms : Return "Lv"
            Case ZuiOptionalKind.ClearCache : Return "Cc"
            Case Else : Return String.Empty
        End Select
    End Function

    Private Function HasZuiInput(kind As ZuiOptionalKind) As Boolean
        If kind = ZuiOptionalKind.None Then Return False
        Return FindInputIndexByNickName(NickNameForZuiKind(kind)) >= 0
    End Function

    Private Function NextZuiKindToInsert() As ZuiOptionalKind
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If Not HasZuiInput(k) Then Return k
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

    Private Shared Function CreateBoolZuiParam(name As String, nick As String, description As String, Optional access As GH_ParamAccess = GH_ParamAccess.item) As Grasshopper.Kernel.Parameters.Param_Boolean
        Return New Grasshopper.Kernel.Parameters.Param_Boolean With {
            .Optional = True,
            .Name = name,
            .NickName = nick,
            .Description = description,
            .Access = access
        }
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
        Return String.Equals(nick, "Oa", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(nick, "Ft", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(nick, "Or", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function FindActiveInputIndex() As Integer
        Return FindInputIndexByNickName("Ac")
    End Function

    Private Function HasAcInputWired() As Boolean
        Dim ix As Integer = FindInputIndexByNickName("Ac")
        Return ix >= 0 AndAlso Params.Input(ix).SourceCount > 0
    End Function

    Friend Function WantsSlotVisible(slot As Integer) As Boolean
        If MyGumball Is Nothing Then Return False
        If Not ViewportPreview.IsLinkedRhinoDocumentActive(Me) Then Return False
        If Locked OrElse Not ViewportPreview.IsEffectivelyPreviewed(Me) Then Return False
        If Params Is Nothing OrElse Params.Input.Count = 0 OrElse Params.Input(0).VolatileData.DataCount = 0 Then Return False
        If SlotSettings Is Nothing OrElse slot < 0 OrElse slot >= SlotSettings.Length Then Return False
        If Not SlotSettings(slot).Active Then Return False
        If HasAcInputWired() Then Return True
        Return Attributes IsNot Nothing AndAlso Attributes.Selected
    End Function

    Friend Function SlotAppliesToAll(slot As Integer) As Boolean
        If ModeValueType = 1 Then Return True
        If SlotSettings Is Nothing OrElse slot < 0 OrElse slot >= SlotSettings.Length Then Return False
        Return SlotSettings(slot).ApplyToAll
    End Function

    ''' <summary>Slots that receive the same transform when <paramref name="gripIndex"/> is dragged with apply-to-all active.</summary>
    Friend Function TransformTargetSlots(gripIndex As Integer) As Integer()
        If MyGumball Is Nothing OrElse gripIndex < 0 OrElse gripIndex >= MyGumball.Count Then
            Return New Integer() {}
        End If
        If ModeValueType = 1 OrElse _aaAppliesToWholeTree Then
            Dim all(MyGumball.Count - 1) As Integer
            For i As Integer = 0 To MyGumball.Count - 1
                all(i) = i
            Next
            Return all
        End If
        If Not SlotAppliesToAll(gripIndex) Then Return New Integer() {gripIndex}
        If _slotPaths Is Nothing OrElse gripIndex >= _slotPaths.Length Then Return New Integer() {gripIndex}
        Dim groupPath As GH_Path = _slotPaths(gripIndex)
        Dim slots As New List(Of Integer)
        For i As Integer = 0 To Math.Min(MyGumball.Count, _slotPaths.Length) - 1
            If _slotPaths(i).Equals(groupPath) Then slots.Add(i)
        Next
        If slots.Count = 0 Then Return New Integer() {gripIndex}
        Return slots.ToArray()
    End Function

    Friend Function IsTransformGroupMember(slot As Integer, gripIndex As Integer) As Boolean
        For Each i As Integer In TransformTargetSlots(gripIndex)
            If i = slot Then Return True
        Next
        Return False
    End Function

    Friend Function EffectiveTransformMode(gripIndex As Integer) As Integer
        If ModeValueType = 2 Then Return 2
        If SlotSettings IsNot Nothing AndAlso gripIndex >= 0 AndAlso gripIndex < SlotSettings.Length AndAlso SlotSettings(gripIndex).Relocate Then Return 2
        If SlotAppliesToAll(gripIndex) Then Return 1
        Return 0
    End Function

    Friend Sub SyncGumballVisibility(Optional suppressRedraw As Boolean = False)
        If MyGumball Is Nothing Then Return
        Dim anyVisible As Boolean = False
        For i As Integer = 0 To MyGumball.Count - 1
            Dim vis As Boolean = WantsSlotVisible(i)
            MyGumball.SetConduitEnabled(i, vis)
            If vis Then anyVisible = True
        Next
        Dim needsSelection As Boolean = Not HasAcInputWired()
        Dim enabled As Boolean = anyVisible AndAlso (Not needsSelection OrElse (Attributes IsNot Nothing AndAlso Attributes.Selected))
        Dim changed As Boolean = False
        If MyGumball.Enabled <> enabled Then
            MyGumball.Enabled = enabled
            changed = True
        End If
        If MyGumball.SyncHoverPollIfNeeded(enabled) Then changed = True
        If Not enabled AndAlso HoverSlot >= 0 Then
            SetHoverTarget(-1, Rhino.UI.Gumball.GumballMode.None)
            changed = True
        End If
        If changed AndAlso Not suppressRedraw Then
            Try
                ViewportPreview.RedrawAllOpenRhinoDocuments()
            Catch
            End Try
        End If
    End Sub

    Friend HoverSlot As Integer = -1
    Friend HoverMode As Rhino.UI.Gumball.GumballMode = Rhino.UI.Gumball.GumballMode.None

    Friend Sub SetHoverTarget(slot As Integer, mode As Rhino.UI.Gumball.GumballMode)
        If slot < -1 Then slot = -1
        If slot < 0 Then mode = Rhino.UI.Gumball.GumballMode.None
        If HoverSlot = slot AndAlso HoverMode = mode Then Return
        HoverSlot = slot
        HoverMode = mode
        If MyGumball IsNot Nothing Then MyGumball.ApplyHoverAppearance(slot, mode)
        Try
            Dim ghDoc As GH_Document = OnPingDocument()
            Dim doc As Rhino.RhinoDoc = If(ghDoc IsNot Nothing, ghDoc.RhinoDocument, Rhino.RhinoDoc.ActiveDoc)
            If doc IsNot Nothing Then doc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Function CreateZuiParam(kind As ZuiOptionalKind) As IGH_Param
        Select Case kind
            Case ZuiOptionalKind.Active
                Return CreateBoolZuiParam("Active", "Ac",
                    "When true, the gumball for that geometry item stays interactive (tree matches G; single value broadcasts). Overrides canvas selection when wired.",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.ApplyToAll
                Return CreateBoolZuiParam("Apply to all", "Aa",
                    "When true, dragging that gumball transforms grouped geometry equally. One boolean for the whole tree affects every item; one boolean per branch affects only that branch (tree matches G).",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.DisplayMode
                Return New Grasshopper.Kernel.Parameters.Param_Integer With {
                    .Optional = True,
                    .Name = "Display mode",
                    .NickName = "Dm",
                    .Description = "Per-item grip preset (tree matches G): 0 = default, 1 = only arrows, 2 = only rotation, 3 = only scale, 4 = free translation.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.AlignToGeometry
                Return CreateAlignGeometryParam()
            Case ZuiOptionalKind.SnapToGeometry
                Return CreateSnapTargetParam()
            Case ZuiOptionalKind.RelocateGumball
                Return CreateRelocateParam()
            Case ZuiOptionalKind.PreserveChanges
                Return CreateBoolZuiParam("List cache", "Pr",
                    "Keep gumball transforms by tree path / list index when geometry moves. With Proximity: keep by index for far moves; proximity remaps wrap-shifts and tree changes.")
            Case ZuiOptionalKind.ProximityCache
                Return CreateBoolZuiParam("Proximity cache", "Px",
                    "Re-attach transforms by nearest cached bounding-box centre on wrap-shifts, culls, grafts, and other list/tree changes. Culled geometry is always saved and restored if it returns.")
            Case ZuiOptionalKind.SaveShifted
                Return CreateBoolZuiParam("Save shifted", "Ss",
                    "Legacy input; ignored. Save-shifted is always active when Proximity cache is on.")
            Case ZuiOptionalKind.LiveTransforms
                Return CreateBoolZuiParam("Live", "Lv",
                    "Refresh downstream Grasshopper while dragging the gumball.")
            Case ZuiOptionalKind.ClearCache
                Return CreateBoolZuiParam("Clear cache", "Cc",
                    "Pulse true to reset gumball and clear cache data (rising edge only).")
        End Select
        Return Nothing
    End Function

    Private Function CreateAlignGeometryParam() As Grasshopper.Kernel.Parameters.Param_Geometry
        Return New Grasshopper.Kernel.Parameters.Param_Geometry With {
            .Optional = True,
            .Name = "Geometry to align",
            .NickName = "A",
            .Description = "Plane or geometry per item (tree matches G) to orient each gumball. Unwired or empty = align off.",
            .Access = GH_ParamAccess.tree
        }
    End Function

    Private Function CreateSnapTargetParam() As Grasshopper.Kernel.Parameters.Param_Geometry
        Return New Grasshopper.Kernel.Parameters.Param_Geometry With {
            .Optional = True,
            .Name = "Snap target",
            .NickName = "S",
            .Description = "Snap target per item (tree matches G) while translating gumball grips. Unwired or empty = snap off.",
            .Access = GH_ParamAccess.tree
        }
    End Function

    Private Function CreateRelocateParam() As Grasshopper.Kernel.Parameters.Param_Geometry
        Return New Grasshopper.Kernel.Parameters.Param_Geometry With {
            .Optional = True,
            .Name = "Relocate gumball",
            .NickName = "R",
            .Description = "Point or plane per item (tree matches G) to place each gumball frame without moving geometry. Unwired or empty = relocate input off.",
            .Access = GH_ParamAccess.tree
        }
    End Function

    Private Sub SetZuiKindEnabled(kind As ZuiOptionalKind, enabled As Boolean)
        If kind = ZuiOptionalKind.None Then Return
        If enabled Then
            If HasZuiInput(kind) Then Return
            Dim param As IGH_Param = CreateZuiParam(kind)
            If param Is Nothing Then Return
            Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
        Else
            Dim ix As Integer = FindInputIndexByNickName(NickNameForZuiKind(kind))
            If ix < 0 Then Return
            If kind = ZuiOptionalKind.AlignToGeometry AndAlso MyGumball IsNot Nothing Then
                MyGumball.ClearAllSlotAlign()
                MyGumball.GeometrytoAlign = Nothing
                MyGumball.ClearAlignAxisReference()
            ElseIf kind = ZuiOptionalKind.SnapToGeometry AndAlso MyGumball IsNot Nothing Then
                MyGumball.DisposeSnapTranslateTargets()
            End If
            Dim p As IGH_Param = Params.Input(ix)
            p.RemoveAllSources()
            Params.UnregisterInputParameter(p)
        End If
        SyncFeatureFlagsFromInputs()
        Params.OnParametersChanged()
    End Sub

    Friend Sub SyncOptionalInputsFromFlags()
        EnsureZuiMatchesFlag(ZuiOptionalKind.DisplayMode, ModeValueAtt <> 0)
        EnsureZuiMatchesFlag(ZuiOptionalKind.AlignToGeometry, ModeValueAlign)
        EnsureZuiMatchesFlag(ZuiOptionalKind.SnapToGeometry, ModeValueSnap)
        VariableParameterMaintenance()
        Params.OnParametersChanged()
    End Sub

    Private Sub SyncFeatureFlagsFromInputs()
        ModeValueAlign = HasZuiInput(ZuiOptionalKind.AlignToGeometry)
        ModeValueSnap = HasZuiInput(ZuiOptionalKind.SnapToGeometry)
        ModeValueRelocate = HasZuiInput(ZuiOptionalKind.RelocateGumball)
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
        Dim ix As Integer = FindInputIndexByNickName(NickNameForZuiKind(kind))
        If ix >= 0 AndAlso ZuiInputWired(ix) Then Return ReadBoolInputVolatile(ix, defaultValue)
        Return defaultValue
    End Function

    Private Function EffectiveDisplayModeForMenu() As Integer
        Dim dmIx As Integer = FindInputIndexByNickName("Dm")
        If dmIx >= 0 AndAlso ZuiInputWired(dmIx) Then
            Return ClampDisplayMode(ReadIntInputVolatile(dmIx, ModeValueAtt))
        End If
        Return ModeValueAtt
    End Function

    Private Sub EnsureZuiMatchesFlag(kind As ZuiOptionalKind, shouldHave As Boolean)
        If shouldHave Then
            If Not HasZuiInput(kind) Then
                Dim param As IGH_Param = CreateZuiParam(kind)
                If param IsNot Nothing Then Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
            End If
        ElseIf HasZuiInput(kind) Then
            Dim ix As Integer = FindInputIndexByNickName(NickNameForZuiKind(kind))
            If ix >= 0 Then
                Dim p As IGH_Param = Params.Input(ix)
                p.RemoveAllSources()
                Params.UnregisterInputParameter(p)
            End If
        End If
    End Sub

    Private Sub ApplyBoolInput(DA As IGH_DataAccess, ix As Integer, ByRef target As Boolean, defaultIfUnwired As Boolean)
        If ix < 0 Then Return
        If Params.Input(ix).SourceCount > 0 Then
            Dim v As Boolean = defaultIfUnwired
            If DA.GetData(ix, v) Then target = v
        End If
        ' Unwired: keep target (menu toggle / Write-Read state); do not force defaultIfUnwired.
    End Sub

    Private Function ReadOptionalBoolInput(DA As IGH_DataAccess, ix As Integer) As Boolean?
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return Nothing
        Dim v As Boolean = False
        If DA.GetData(ix, v) Then Return v
        Return Nothing
    End Function

    Private Sub ApplyApplyToAllFromInputs(DA As IGH_DataAccess)
        Dim aaIx As Integer = FindInputIndexByNickName("Aa")
        If aaIx < 0 OrElse Params.Input(aaIx).SourceCount = 0 Then Return
        ' Per-item Aa is resolved in BuildSlotSettings; single-value trees are handled there too.
    End Sub

    Private Function IsLiveGripDragActive() As Boolean
        Return LiveTransformsWhileDragging AndAlso MyGumball IsNot Nothing AndAlso
            MyGumball.PreviewGripSlot >= 0 AndAlso Not (MyGumball.PreviewGripDelta = Transform.Identity)
    End Function

    Private Shared Function ClampDisplayMode(mode As Integer) As Integer
        If mode < 0 OrElse mode > 4 Then Return 0
        Return mode
    End Function

    ''' <summary>Maps pre–display-mode-integer attmode values saved in older GH files.</summary>
    Friend Shared Function MigrateLegacyAttMode(legacy As Integer) As Integer
        Select Case legacy
            Case 1 : Return 1
            Case 2 : Return 4
            Case 3 : Return 2
            Case Else : Return ClampDisplayMode(legacy)
        End Select
    End Function

    Friend Shared Function LoadDisplayModeFromChunk(att1 As GH_IO.Serialization.GH_Chunk) As Integer
        If att1 Is Nothing Then Return 0
        Dim dm As Integer = 0
        If att1.TryGetInt32("displaymode", 7, dm) Then Return ClampDisplayMode(dm)
        Return MigrateLegacyAttMode(att1.GetInt32("attmode", 0))
    End Function

    Private Function HasLegacyDisplayModeZui() As Boolean
        Return FindInputIndexByNickName("Oa") >= 0 OrElse
            FindInputIndexByNickName("Ft") >= 0 OrElse
            FindInputIndexByNickName("Or") >= 0
    End Function

    Friend Shared Function BuildAppearancePresetForAtt(att As Integer, preserveFrom As Integer()) As Integer()
        Dim ca As Integer() = preserveFrom
        Select Case ClampDisplayMode(att)
            Case 1
                Return New Integer(9) {1, 0, 2, 0, 0, ca(5), ca(6), ca(7), ca(8), ca(9)}
            Case 2
                Return New Integer(9) {0, 0, 0, 1, 0, ca(5), ca(6), ca(7), ca(8), ca(9)}
            Case 3
                Return New Integer(9) {0, 0, 0, 0, 1, ca(5), ca(6), ca(7), ca(8), ca(9)}
            Case 4
                Return New Integer(9) {0, 0, 2, 0, 0, ca(5), ca(6), ca(7), ca(8), ca(9)}
            Case Else
                Return New Integer(9) {1, 1, 2, 1, 1, ca(5), ca(6), ca(7), ca(8), ca(9)}
        End Select
    End Function

    Friend Shared Function AppearancePresetsEqual(a As Integer(), b As Integer()) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Length <> b.Length Then Return False
        For i As Integer = 0 To a.Length - 1
            If a(i) <> b(i) Then Return False
        Next
        Return True
    End Function

    Private Sub ApplyStoredDisplayModeIfNeeded(DA As IGH_DataAccess)
        ' Display modes are applied per slot in BuildSlotSettings / ApplyPerSlotDisplayModes.
    End Sub

    Private Sub ApplyModeValueAtt(att As Integer)
        ModeValueAtt = ClampDisplayMode(att)
        If MyGumball Is Nothing Then Return
        If SlotSettings IsNot Nothing Then
            For i As Integer = 0 To SlotSettings.Length - 1
                SlotSettings(i).DisplayMode = ModeValueAtt
            Next
        End If
        For i As Integer = 0 To MyGumball.Count - 1
            MyGumball.ApplyAppearancePresetToSlot(i, ModeValueAtt)
        Next
        MyGumball.RefreshConduitDisplays()
    End Sub

    Private Function DetectWholeTreeBoolBroadcast(DA As IGH_DataAccess, nick As String) As Boolean
        Dim ix As Integer = FindInputIndexByNickName(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return False
        Dim tree As New GH_Structure(Of GH_Boolean)
        If Not DA.GetDataTree(ix, tree) Then Return False
        Return tree.DataCount = 1
    End Function

    Private Sub MapBoolTreeToSlots(DA As IGH_DataAccess, nick As String, geom As DataTree(Of GeometryBase),
                                   defaultValue As Boolean, apply As Action(Of Integer, Boolean))
        If SlotSettings Is Nothing OrElse _leafToGumballSlot Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNickName(nick)
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

        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of GH_Boolean) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < SlotSettings.Length Then
                        Dim v As Boolean = defaultValue
                        If useBroadcast Then
                            v = broadcast
                        ElseIf valueBranch IsNot Nothing Then
                            If valueBranch.Count = 1 AndAlso valueBranch(0) IsNot Nothing Then
                                v = valueBranch(0).Value
                            ElseIf j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                                v = valueBranch(j).Value
                            End If
                        End If
                        apply(slot, v)
                    End If
                End If
                leafIx += 1
            Next
        Next
    End Sub

    Private Sub MapIntTreeToSlots(DA As IGH_DataAccess, nick As String, geom As DataTree(Of GeometryBase),
                                  defaultValue As Integer, apply As Action(Of Integer, Integer))
        If SlotSettings Is Nothing OrElse _leafToGumballSlot Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNickName(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Integer)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Integer = defaultValue
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gi As GH_Integer = tree.AllData(True).FirstOrDefault()
            If gi IsNot Nothing Then broadcast = gi.Value
        End If

        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of GH_Integer) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < SlotSettings.Length Then
                        Dim v As Integer = defaultValue
                        If useBroadcast Then
                            v = broadcast
                        ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                            v = valueBranch(j).Value
                        End If
                        apply(slot, ClampDisplayMode(v))
                    End If
                End If
                leafIx += 1
            Next
        Next
    End Sub

    Private Sub ApplyLegacyDisplayModeToSlots(DA As IGH_DataAccess)
        If SlotSettings Is Nothing OrElse Not HasLegacyDisplayModeZui() Then Return
        Dim orIx As Integer = FindInputIndexByNickName("Or")
        Dim ftIx As Integer = FindInputIndexByNickName("Ft")
        Dim oaIx As Integer = FindInputIndexByNickName("Oa")
        Dim orOn As Boolean? = ReadOptionalBoolInput(DA, orIx)
        Dim ftOn As Boolean? = ReadOptionalBoolInput(DA, ftIx)
        Dim oaOn As Boolean? = ReadOptionalBoolInput(DA, oaIx)
        Dim mode As Integer = ModeValueAtt
        If orOn.HasValue AndAlso orOn.Value Then
            mode = 2
        ElseIf ftOn.HasValue AndAlso ftOn.Value Then
            mode = 4
        ElseIf oaOn.HasValue AndAlso oaOn.Value Then
            mode = 1
        ElseIf (orOn.HasValue AndAlso Not orOn.Value) OrElse (ftOn.HasValue AndAlso Not ftOn.Value) OrElse (oaOn.HasValue AndAlso Not oaOn.Value) Then
            mode = 0
        End If
        mode = ClampDisplayMode(mode)
        For i As Integer = 0 To SlotSettings.Length - 1
            SlotSettings(i).DisplayMode = mode
        Next
        ModeValueAtt = mode
    End Sub

    Private Sub BuildSlotSettings(DA As IGH_DataAccess, geom As DataTree(Of GeometryBase))
        Dim n As Integer = If(MyGumball Is Nothing, 0, MyGumball.Count)
        If n <= 0 Then
            SlotSettings = Nothing
            _slotPaths = Nothing
            _aaAppliesToWholeTree = False
            Return
        End If
        ReDim SlotSettings(n - 1)
        For i As Integer = 0 To n - 1
            SlotSettings(i).Active = True
            SlotSettings(i).ApplyToAll = (ModeValueType = 1)
            SlotSettings(i).DisplayMode = ModeValueAtt
            SlotSettings(i).Relocate = (ModeValueType = 2)
        Next

        MapBoolTreeToSlots(DA, "Ac", geom, True, Sub(slot, v) SlotSettings(slot).Active = v)
        _aaAppliesToWholeTree = DetectWholeTreeBoolBroadcast(DA, "Aa")
        MapBoolTreeToSlots(DA, "Aa", geom, ModeValueType = 1, Sub(slot, v) SlotSettings(slot).ApplyToAll = v)
        If FindInputIndexByNickName("Dm") >= 0 Then
            MapIntTreeToSlots(DA, "Dm", geom, ModeValueAtt, Sub(slot, v) SlotSettings(slot).DisplayMode = v)
        ElseIf HasLegacyDisplayModeZui() Then
            ApplyLegacyDisplayModeToSlots(DA)
        End If
        MapRelocateTreeToSlots(DA, geom)
    End Sub

    Private Sub MapRelocateTreeToSlots(DA As IGH_DataAccess, geom As DataTree(Of GeometryBase))
        If SlotSettings Is Nothing OrElse _leafToGumballSlot Is Nothing Then Return
        Dim rIx As Integer = FindInputIndexByNickName("R")
        If rIx < 0 OrElse Params.Input(rIx).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of Types.IGH_GeometricGoo)
        If Not DA.GetDataTree(rIx, tree) Then Return

        Dim broadcastGoo As Types.IGH_GeometricGoo = Nothing
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            broadcastGoo = tree.AllData(True).FirstOrDefault()
        End If

        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of Types.IGH_GeometricGoo) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < SlotSettings.Length Then
                        Dim gg As Types.IGH_GeometricGoo = Nothing
                        If useBroadcast Then
                            gg = broadcastGoo
                        ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count Then
                            gg = valueBranch(j)
                        End If
                        If gg IsNot Nothing AndAlso RelocateGooIsValid(gg) Then
                            SlotSettings(slot).Relocate = True
                        End If
                    End If
                End If
                leafIx += 1
            Next
        Next
    End Sub

    Private Shared Function RelocateGooIsValid(gg As Types.IGH_GeometricGoo) As Boolean
        If gg Is Nothing Then Return False
        Dim pl As New Plane
        If TryUnpackAlignPlaneFromGoo(gg, pl) Then Return pl.IsValid
        Dim ptGoo As GH_Point = TryCast(gg, GH_Point)
        If ptGoo IsNot Nothing Then Return ptGoo.IsValid
        Dim pt As Point3d
        Return GH_Convert.ToPoint3d(gg, pt, GH_Conversion.Primary) AndAlso pt.IsValid
    End Function

    Private Sub ApplyPerSlotDisplayModes()
        If MyGumball Is Nothing OrElse SlotSettings Is Nothing Then Return
        If IsLiveGripDragActive() Then Return
        If MyGumball.IsNumericGripPickActive() Then Return
        For i As Integer = 0 To Math.Min(MyGumball.Count, SlotSettings.Length) - 1
            MyGumball.ApplyAppearancePresetToSlot(i, SlotSettings(i).DisplayMode)
        Next
        MyGumball.RefreshConduitDisplays()
    End Sub

    Private Sub ApplyGlobalZuiInputs(DA As IGH_DataAccess)
        ApplyAlignSnapModesFromInputs(DA)
        ApplyBoolInput(DA, FindInputIndexByNickName("Pr"), PreserveTransformsOnGeometryChange, False)
        ApplyBoolInput(DA, FindInputIndexByNickName("Px"), ProximityCache, False)
        If ProximityCache Then SaveShifted = True Else SaveShifted = False
        ApplyBoolInput(DA, FindInputIndexByNickName("Lv"), LiveTransformsWhileDragging, False)

        Dim ccIx As Integer = FindInputIndexByNickName("Cc")
        If ccIx >= 0 AndAlso Params.Input(ccIx).SourceCount > 0 Then
            Dim pulse As Boolean = False
            If DA.GetData(ccIx, pulse) Then
                If pulse AndAlso Not _clearCacheInputPrev Then ClearGumballCacheInternal()
                _clearCacheInputPrev = pulse
            End If
        Else
            _clearCacheInputPrev = False
        End If
    End Sub

    Private Sub ApplyZuiBooleanInputs(DA As IGH_DataAccess)
        ApplyApplyToAllFromInputs(DA)
        ApplyGlobalZuiInputs(DA)
    End Sub

    Private Sub ApplyAlignSnapModesFromInputs(DA As IGH_DataAccess)
        Dim aIx As Integer = FindInputIndexByNickName("A")
        If aIx >= 0 Then
            ModeValueAlign = Params.Input(aIx).SourceCount > 0 AndAlso Params.Input(aIx).VolatileDataCount > 0
        End If
        Dim sIx As Integer = FindInputIndexByNickName("S")
        If sIx >= 0 Then
            ModeValueSnap = Params.Input(sIx).SourceCount > 0 AndAlso Params.Input(sIx).VolatileDataCount > 0
        End If
    End Sub

    Private Sub ClearGumballCacheInternal()
        Me.Cache.Clear()
        CacheTreeKeys = Nothing
        ShiftedGumballEntries.Clear()
        If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
        Me.MyGumball = Nothing
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

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
        If side = GH_ParameterSide.Input AndAlso index >= BaseInputCount AndAlso index < Params.Input.Count Then
            Dim nick As String = Params.Input(index).NickName
            If String.Equals(nick, "A", StringComparison.OrdinalIgnoreCase) AndAlso MyGumball IsNot Nothing Then
                MyGumball.ClearAllSlotAlign()
            ElseIf String.Equals(nick, "S", StringComparison.OrdinalIgnoreCase) AndAlso MyGumball IsNot Nothing Then
                MyGumball.DisposeSnapTranslateTargets()
            End If
        End If
        Return True
    End Function

    Public Sub VariableParameterMaintenance() Implements IGH_VariableParameterComponent.VariableParameterMaintenance
        SyncFeatureFlagsFromInputs()
    End Sub

#End Region

    Private Function FindInputIndexByNickName(nick As String) As Integer
        For i As Integer = 0 To Params.Input.Count - 1
            If String.Equals(Params.Input(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Sub SyncSecondaryInputs()
        SyncOptionalInputsFromFlags()
        Me.ExpireSolution(True)
    End Sub

    ''' <summary>Restore optional A/S inputs from GH file without triggering <see cref="ModeValue"/> setter side effects twice.</summary>
    Friend Sub ApplyOptionalInputModesFromFile(alignGeometry As Boolean, snapToGeometry As Boolean)
        ModeValueAlign = alignGeometry
        ModeValueSnap = snapToGeometry
        SyncOptionalInputsFromFlags()
    End Sub

    Private Sub RefreshSnapTranslateTargets(DA As IGH_DataAccess, geom As DataTree(Of GeometryBase))
        If MyGumball Is Nothing Then Return
        MyGumball.DisposeSnapTranslateTargets()
        If Not Double.IsNaN(SnapTranslateTolerance) AndAlso SnapTranslateTolerance > 0 Then
            MyGumball.SnapTranslateRadiusOverride = SnapTranslateTolerance
        Else
            MyGumball.SnapTranslateRadiusOverride = Double.NaN
        End If
        If Not ModeValueSnap Then Return

        Dim ix As Integer = FindInputIndexByNickName("S")
        If ix < 0 OrElse Params.Input(ix).VolatileDataCount = 0 Then Return
        If _leafToGumballSlot Is Nothing OrElse geom Is Nothing OrElse geom.DataCount = 0 Then Return

        Dim snapTree As New GH_Structure(Of Types.IGH_GeometricGoo)
        If Not DA.GetDataTree(ix, snapTree) Then Return

        Dim broadcastGoo As Types.IGH_GeometricGoo = Nothing
        Dim useBroadcast As Boolean = False
        If snapTree.DataCount = 1 Then
            useBroadcast = True
            broadcastGoo = snapTree.AllData(True).FirstOrDefault()
        End If

        Dim perSlot(MyGumball.Count - 1) As List(Of GeometryBase)
        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of Types.IGH_GeometricGoo) = Nothing
            If Not useBroadcast AndAlso snapTree.PathExists(path) Then valueBranch = snapTree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < MyGumball.Count Then
                        Dim gg As Types.IGH_GeometricGoo = Nothing
                        If useBroadcast Then
                            gg = broadcastGoo
                        ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count Then
                            gg = valueBranch(j)
                        End If
                        AppendSnapGooToSlot(perSlot, slot, gg)
                    End If
                End If
                leafIx += 1
            Next
        Next

        MyGumball.SlotSnapTranslateTargets = perSlot
    End Sub

    Private Shared Sub AppendSnapGooToSlot(perSlot As List(Of GeometryBase)(), slot As Integer, gg As Types.IGH_GeometricGoo)
        If gg Is Nothing OrElse perSlot Is Nothing OrElse slot < 0 OrElse slot >= perSlot.Length Then Return
        Dim gb As GeometryBase = GH_Convert.ToGeometryBase(gg)
        If gb Is Nothing Then Return
        If perSlot(slot) Is Nothing Then perSlot(slot) = New List(Of GeometryBase)
        perSlot(slot).Add(gb.Duplicate())
    End Sub

    Public Property ModeValue(ByVal index As Integer) As Integer
        'Gumball mode = 0
        'Gumball attributes = 1
        'Align gumball = 2
        'Snap to geometry + S input = 3
        Get
            Select Case index
                Case 0
                    Return ModeValueType
                Case 1
                    Return ModeValueAtt
                Case 2
                    Return ModeValueAlign
                Case 3
                    Return CInt(ModeValueSnap)
                Case Else
                    Throw New ArgumentOutOfRangeException()
            End Select
        End Get
        Set(value As Integer)

            Select Case index
                Case 0
                    ModeValueType = value

                    Select Case value
                        Case 0
                            Me.Message = String.Empty
                        Case 1
                            Me.Message = "Apply to all"
                        Case 2
                            Me.Message = "Relocate"
                    End Select

                    Me.ExpireSolution(True)
                Case 1
                    ApplyModeValueAtt(ClampDisplayMode(value))
                Case 2
                    ModeValueAlign = value
                    SyncSecondaryInputs()
                Case 3
                    ModeValueSnap = CBool(value)
                    SyncSecondaryInputs()
                Case Else
                    Throw New ArgumentOutOfRangeException()
            End Select
        End Set
    End Property

    Public MyGumball As GhGumball
    ''' <summary>Rhino model this gumball belongs to (captured while the GH definition is enabled).</summary>
    Private _previewRhinoDoc As Rhino.RhinoDoc = Nothing

    Friend ReadOnly Property PreviewRhinoDoc As Rhino.RhinoDoc
        Get
            Return _previewRhinoDoc
        End Get
    End Property

    Friend Sub SetPreviewRhinoDoc(doc As Rhino.RhinoDoc)
        _previewRhinoDoc = doc
    End Sub

    Private Cache As New DataTree(Of GeometryBase)
    Private Paths As GH_Path()
    ''' <summary>Parallel to flattened input leaves: index into MyGumball geometry/Xform (-1 means null input at that leaf).</summary>
    Private _leafToGumballSlot As Integer()
    ''' <summary>Input tree path per gumball slot (parallel to MyGumball geometry indices).</summary>
    Private _slotPaths As GH_Path()
    ''' <summary>True when wired Aa is a single boolean broadcast to the whole geometry tree.</summary>
    Private _aaAppliesToWholeTree As Boolean = False
    ''' <summary>Maximum snap distance while translating (model units); NaN = automatic from document tolerance.</summary>
    Public SnapTranslateTolerance As Double = Double.NaN

    Public AttForm As FormAttributes = Nothing
    Private MyGumballAttributes As Integer() = New Integer(9) {1, 1, 2, 1, 1, 50, 5, 2, 15, 35}

    Private ModeValueType As New Integer
    Private ModeValueAlign As New Boolean
    ''' <summary>Right-click Snap to geometry: optional input S feeds translator snap targets.</summary>
    Private ModeValueSnap As Boolean
    ''' <summary>Optional input R feeds per-item gumball relocation targets.</summary>
    Private ModeValueRelocate As Boolean
    Private ModeValueAtt As New Integer

    ''' <summary>When true, upstream geometry updates try to keep existing gumball transform stacks per non-null index (list cache). With ProximityCache: mixed mode.</summary>
    Public Property PreserveTransformsOnGeometryChange As Boolean
        Get
            Return _preserveTransformsOnGeometryChange
        End Get
        Set(value As Boolean)
            _preserveTransformsOnGeometryChange = value
        End Set
    End Property

    Private _preserveTransformsOnGeometryChange As Boolean = False

    ''' <summary>When true, Gumball viewport drags refresh downstream Grasshopper outputs during the drag (preview only); geometry and transforms are committed once on mouse-up, like built-in Grasshopper point/parameters.</summary>
    Public Property LiveTransformsWhileDragging As Boolean
        Get
            Return _liveTransformsWhileDragging
        End Get
        Set(value As Boolean)
            _liveTransformsWhileDragging = value
        End Set
    End Property

    Private _liveTransformsWhileDragging As Boolean

    ''' <summary>
    ''' When the list/tree structure changes, remap transforms by greedy nearest centroid (from cached upstream geometry).
    ''' Save-shifted is always on with this flag. With list cache: mixed mode (index keep when tree unchanged).
    ''' </summary>
    Public Property ProximityCache As Boolean
        Get
            Return _proximityCacheOnGeometryChange
        End Get
        Set(value As Boolean)
            _proximityCacheOnGeometryChange = value
        End Set
    End Property

    Private _proximityCacheOnGeometryChange As Boolean = False

    ''' <summary>Always mirrors ProximityCache. Kept for serialization / undo compatibility.</summary>
    Public Property SaveShifted As Boolean
        Get
            Return _saveShiftedGumballTransforms
        End Get
        Set(value As Boolean)
            _saveShiftedGumballTransforms = value
        End Set
    End Property

    Private _saveShiftedGumballTransforms As Boolean = False

    Friend ShiftedGumballEntries As New List(Of ShiftedGumballEntry)

    ''' <summary>Stable tree-structure fingerprint for mixed list/proximity cache.</summary>
    Private CacheTreeKeys As List(Of String) = Nothing

    Private Shared Function CloneGhTransform(src As Types.GH_Transform) As Types.GH_Transform
        Dim result As New Types.GH_Transform()
        If src Is Nothing Then Return result
        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In src.CompoundTransforms
            result.CompoundTransforms.Add(t.Duplicate())
        Next
        result.ClearCaches()
        Return result
    End Function

    Private Shared Function HasMeaningfulXform(xf As Types.GH_Transform) As Boolean
        Return xf IsNot Nothing AndAlso xf.CompoundTransforms IsNot Nothing AndAlso xf.CompoundTransforms.Count > 0
    End Function

    Private Sub AddShiftedGumballEntry(key As GeometryProximityKey, xf As Types.GH_Transform)
        For Each existing As ShiftedGumballEntry In ShiftedGumballEntries
            If ProximityMatching.ShiftedKeyMatchesCandidate(existing.Key, key) Then Return
        Next
        ShiftedGumballEntries.Add(New ShiftedGumballEntry With {
            .Key = key,
            .Xform = CloneGhTransform(xf)
        })
    End Sub

    Private Sub RemoveShiftedGumballEntriesMatching(key As GeometryProximityKey)
        ShiftedGumballEntries.RemoveAll(Function(e) ProximityMatching.ShiftedKeyMatchesCandidate(e.Key, key))
    End Sub

    Friend Sub RememberShiftedGumballTransforms(oldGb As GhGumball, oldCacheGeoms As GeometryBase(), newGeoms As GeometryBase(), slotMap As Integer())
        If oldGb Is Nothing OrElse oldCacheGeoms Is Nothing OrElse newGeoms Is Nothing Then Return
        Dim nOld As Integer = Math.Min(oldGb.Count, oldCacheGeoms.Length)
        Dim matchedOld As New HashSet(Of Integer)
        If slotMap IsNot Nothing Then
            For Each oldIx As Integer In slotMap
                If oldIx >= 0 Then matchedOld.Add(oldIx)
            Next
        End If
        For oi As Integer = 0 To nOld - 1
            If matchedOld.Contains(oi) Then
                Dim ka As GeometryProximityKey = Nothing
                If ProximityMatching.TryGetProximityKey(oldCacheGeoms(oi), ka) Then
                    If ProximityMatching.OldGeometryStillInList(oldCacheGeoms, oi, newGeoms) Then
                        RemoveShiftedGumballEntriesMatching(ka)
                    End If
                End If
                Continue For
            End If
            If oi >= oldGb.Count OrElse Not HasMeaningfulXform(oldGb.Xform(oi)) Then Continue For
            Dim key As GeometryProximityKey = Nothing
            If Not ProximityMatching.TryGetProximityKey(oldCacheGeoms(oi), key) Then Continue For
            AddShiftedGumballEntry(key, oldGb.Xform(oi))
        Next
    End Sub

    Friend Sub ApplyShiftedGumballTransforms(gb As GhGumball, newGeoms As GeometryBase(), slotMap As Integer())
        If gb Is Nothing OrElse newGeoms Is Nothing OrElse ShiftedGumballEntries.Count = 0 Then Return
        Dim usedSaved As New HashSet(Of Integer)
        For j As Integer = 0 To Math.Min(gb.Count, newGeoms.Length) - 1
            If slotMap IsNot Nothing AndAlso j < slotMap.Length AndAlso slotMap(j) >= 0 Then Continue For
            If HasMeaningfulXform(gb.Xform(j)) Then Continue For
            Dim kb As GeometryProximityKey = Nothing
            If Not ProximityMatching.TryGetProximityKey(newGeoms(j), kb) Then Continue For
            For si As Integer = 0 To ShiftedGumballEntries.Count - 1
                If usedSaved.Contains(si) Then Continue For
                If Not ProximityMatching.ShiftedKeyMatchesCandidate(ShiftedGumballEntries(si).Key, kb) Then Continue For
                gb.Xform(j) = CloneGhTransform(ShiftedGumballEntries(si).Xform)
                GhGumball.ApplyCompoundGenericTransformsInOrder(gb.Geometry(j), gb.Xform(j))
                gb.RebuildGumballObjectAndConduitAt(j)
                usedSaved.Add(si)
                Exit For
            Next
        Next
        If usedSaved.Count > 0 Then
            Dim remaining As New List(Of ShiftedGumballEntry)
            For si As Integer = 0 To ShiftedGumballEntries.Count - 1
                If Not usedSaved.Contains(si) Then remaining.Add(ShiftedGumballEntries(si))
            Next
            ShiftedGumballEntries = remaining
        End If
    End Sub

    Private Shared Function TryUnpackAlignPlaneFromGoo(gg As Types.IGH_GeometricGoo, ByRef pl As Plane) As Boolean
        Dim ghp As GH_Plane = TryCast(gg, GH_Plane)
        If ghp IsNot Nothing AndAlso ghp.IsValid Then
            pl = ghp.Value
            Return pl.IsValid
        End If
        Dim fromConvert As New Plane
        If GH_Convert.ToPlane(gg, fromConvert, GH_Conversion.Primary) AndAlso fromConvert.IsValid Then
            pl = fromConvert
            Return True
        End If
        Return False
    End Function

    ''' <summary>Same axis directions (right-handed); plane origin is ignored for comparison.</summary>
    Private Shared Function AlignAxisFramesEqual(a As Plane, b As Plane) As Boolean
        Const ang As Double = 0.002
        If Not a.IsValid OrElse Not b.IsValid Then Return False
        If Not a.ZAxis.IsParallelTo(b.ZAxis, ang) OrElse a.ZAxis * b.ZAxis <= 0 Then Return False
        If Not a.XAxis.IsParallelTo(b.XAxis, ang) OrElse a.XAxis * b.XAxis <= 0 Then Return False
        Return True
    End Function

    Private Sub ApplyAlignGeometryInput(DA As IGH_DataAccess, geom As DataTree(Of GeometryBase))
        Dim apIx As Integer = FindInputIndexByNickName("A")
        If apIx < 0 OrElse MyGumball Is Nothing OrElse Not ModeValueAlign Then Return
        If IsLiveGripDragActive() Then Return
        If MyGumball.IsNumericGripPickActive() Then Return

        If Params.Input(apIx).VolatileDataCount = 0 Then
            MyGumball.ClearAllSlotAlign()
            MyGumball.GeometrytoAlign = Nothing
            MyGumball.ClearAlignAxisReference()
            Return
        End If

        Dim alignTree As New GH_Structure(Of Types.IGH_GeometricGoo)
        If Not DA.GetDataTree(apIx, alignTree) Then Return

        Dim broadcastGoo As Types.IGH_GeometricGoo = Nothing
        Dim useBroadcast As Boolean = False
        If alignTree.DataCount = 1 Then
            useBroadcast = True
            broadcastGoo = alignTree.AllData(True).FirstOrDefault()
        End If

        Dim slotHasAlign(MyGumball.Count - 1) As Boolean
        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of Types.IGH_GeometricGoo) = Nothing
            If Not useBroadcast AndAlso alignTree.PathExists(path) Then valueBranch = alignTree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < MyGumball.Count Then
                        Dim gg As Types.IGH_GeometricGoo = Nothing
                        If useBroadcast Then
                            gg = broadcastGoo
                        ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count Then
                            gg = valueBranch(j)
                        End If
                        If gg IsNot Nothing Then
                            ApplyAlignGooToSlot(slot, gg)
                            slotHasAlign(slot) = True
                        End If
                    End If
                End If
                leafIx += 1
            Next
        Next

        For i As Integer = 0 To MyGumball.Count - 1
            If Not slotHasAlign(i) Then MyGumball.ClearSlotAlign(i)
        Next
        SyncGumballVisibility()
    End Sub

    Private Sub ApplyRelocateGooToSlot(slot As Integer, gg As Types.IGH_GeometricGoo)
        If MyGumball Is Nothing OrElse gg Is Nothing Then Return
        Dim axisPl As New Plane
        If TryUnpackAlignPlaneFromGoo(gg, axisPl) Then
            MyGumball.RelocateSlotToPlane(slot, axisPl)
            Return
        End If
        Dim ptGoo As GH_Point = TryCast(gg, GH_Point)
        If ptGoo IsNot Nothing AndAlso ptGoo.IsValid Then
            MyGumball.RelocateSlotToPoint(slot, ptGoo.Value)
            Return
        End If
        Dim pt As Point3d
        If GH_Convert.ToPoint3d(gg, pt, GH_Conversion.Primary) AndAlso pt.IsValid Then
            MyGumball.RelocateSlotToPoint(slot, pt)
        End If
    End Sub

    Private Sub ApplyRelocateGeometryInput(DA As IGH_DataAccess, geom As DataTree(Of GeometryBase))
        Dim rIx As Integer = FindInputIndexByNickName("R")
        If rIx < 0 OrElse MyGumball Is Nothing OrElse Not ModeValueRelocate Then Return
        If IsLiveGripDragActive() Then Return
        If MyGumball.IsNumericGripPickActive() Then Return

        If Params.Input(rIx).VolatileDataCount = 0 Then Return

        Dim relocateTree As New GH_Structure(Of Types.IGH_GeometricGoo)
        If Not DA.GetDataTree(rIx, relocateTree) Then Return

        Dim broadcastGoo As Types.IGH_GeometricGoo = Nothing
        Dim useBroadcast As Boolean = False
        If relocateTree.DataCount = 1 Then
            useBroadcast = True
            broadcastGoo = relocateTree.AllData(True).FirstOrDefault()
        End If

        Dim leafIx As Integer = 0
        For bi As Integer = 0 To geom.BranchCount - 1
            Dim path As GH_Path = geom.Paths(bi)
            Dim valueBranch As IList(Of Types.IGH_GeometricGoo) = Nothing
            If Not useBroadcast AndAlso relocateTree.PathExists(path) Then valueBranch = relocateTree.Branch(path)
            For j As Integer = 0 To geom.Branch(bi).Count - 1
                If leafIx < _leafToGumballSlot.Length Then
                    Dim slot As Integer = _leafToGumballSlot(leafIx)
                    If slot >= 0 AndAlso slot < MyGumball.Count Then
                        Dim gg As Types.IGH_GeometricGoo = Nothing
                        If useBroadcast Then
                            gg = broadcastGoo
                        ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count Then
                            gg = valueBranch(j)
                        End If
                        If gg IsNot Nothing AndAlso RelocateGooIsValid(gg) Then
                            ApplyRelocateGooToSlot(slot, gg)
                        End If
                    End If
                End If
                leafIx += 1
            Next
        Next
        SyncGumballVisibility()
    End Sub

    Private Sub ApplyAlignGooToSlot(slot As Integer, gg As Types.IGH_GeometricGoo)
        If MyGumball Is Nothing OrElse gg Is Nothing Then Return
        Dim axisPl As New Plane
        If TryUnpackAlignPlaneFromGoo(gg, axisPl) Then
            MyGumball.SetSlotAlignAxis(slot, axisPl)
            Return
        End If
        Dim gbAlign As GeometryBase = GH_Convert.ToGeometryBase(gg)
        If gbAlign Is Nothing Then Return
        MyGumball.SetSlotAlignGeometry(slot, gbAlign.Duplicate())
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim Data As New GH_Structure(Of Types.IGH_GeometricGoo)
        Dim InputData As New DataTree(Of GeometryBase)

        'Get input data.
        If Not (DA.GetDataTree(0, Data)) Then Exit Sub

        ViewportPreview.EnsurePreviewRhinoDoc(Me)

        ' Global ZUI flags (per-item inputs are resolved after geometry slots exist).
        ApplyGlobalZuiInputs(DA)

        ' Align input is applied after MyGumball exists (see ApplyAlignGeometryInput).

        'GeometryGoo to GeometryBase (nulls preserved as Nothing per leaf).
        For Each b As GH_Path In Data.Paths
            For Each d As Types.IGH_GeometricGoo In Data.DataList(b)
                If d Is Nothing Then
                    InputData.Add(Nothing, b)
                Else
                    Dim baseGeo As GeometryBase = Grasshopper.Kernel.GH_Convert.ToGeometryBase(d)
                    If baseGeo Is Nothing Then
                        InputData.Add(Nothing, b)
                    Else
                        InputData.Add(baseGeo.Duplicate(), b)
                    End If
                End If
            Next
        Next

        Dim nonNullGeom As New List(Of GeometryBase)(InputData.DataCount)
        Dim newSlotPaths As New List(Of GH_Path)
        Dim newSlotBranch As New List(Of Integer)
        Dim leafCount As Integer = InputData.DataCount
        If leafCount = 0 Then
            _leafToGumballSlot = Nothing
        Else
            _leafToGumballSlot = New Integer(leafCount - 1) {}
            Dim leafIx As Integer = 0
            For bi As Int32 = 0 To InputData.BranchCount - 1
                For lj As Int32 = 0 To InputData.Branch(bi).Count - 1
                    Dim gbLeaf As GeometryBase = InputData.Branch(bi)(lj)
                    If gbLeaf Is Nothing Then
                        _leafToGumballSlot(leafIx) = -1
                    Else
                        Dim dup As GeometryBase = gbLeaf.Duplicate()
                        dup.MakeDeformable()
                        _leafToGumballSlot(leafIx) = nonNullGeom.Count
                        nonNullGeom.Add(dup)
                        newSlotPaths.Add(InputData.Paths(bi))
                        newSlotBranch.Add(lj)
                    End If
                    leafIx += 1
                Next
            Next
        End If
        _slotPaths = newSlotPaths.ToArray()

        'Set cache.
        If (Cache.DataCount = 0) OrElse CacheTreeKeys Is Nothing Then
            SetCache(InputData)
            StoreGumballTreeKeys(newSlotPaths, newSlotBranch)
            If ProximityCache AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                ApplyShiftedGumballTransforms(MyGumball, nonNullGeom.ToArray(), Nothing)
            End If
        Else
            'Test if new inputdata
            If Not AreEquals(Cache, InputData) AndAlso Not ShouldDeferGumballResync() Then
                Dim newTreeKeys As List(Of String) = BuildTreeKeys(newSlotPaths, newSlotBranch)
                Dim treeChanged As Boolean = Not TreeKeysEqual(CacheTreeKeys, newTreeKeys)
                Dim resynced As GhGumball = Nothing

                If ProximityCache AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                    ' Proximity first. When list cache is also on and tree keys are unchanged, still use proximity
                    ' for wrap-shifts/reorders (non-identity map); only keep-by-index when the proximity map is identity
                    ' (same-index match or far move with no matches).
                    resynced = GhGumball.CreateResyncPreservingTransformsProximity(
                        nonNullGeom.ToArray(), Me, MyGumball, Cache, newSlotPaths, newSlotBranch,
                        preferIndexIfIdentity:=PreserveTransformsOnGeometryChange AndAlso Not treeChanged)
                ElseIf PreserveTransformsOnGeometryChange AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                    ' List cache only: keep transforms by index.
                    resynced = GhGumball.CreateResyncPreservingTransforms(nonNullGeom.ToArray(), Me, MyGumball)
                End If

                If resynced Is Nothing AndAlso ProximityCache AndAlso MyGumball IsNot Nothing Then
                    GhGumball.RememberShiftedTransformsOnClear(Me, MyGumball, Cache)
                End If
                Cache.Clear()
                If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
                MyGumball = Nothing
                SetCache(InputData)
                StoreGumballTreeKeys(newSlotPaths, newSlotBranch)
                If resynced IsNot Nothing Then
                    MyGumball = resynced
                    If Me.ModeValue(2) Then MyGumball.ReapplyStoredAlignment()
                    SyncGumballVisibility()
                End If
            ElseIf ProximityCache AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                ApplyShiftedGumballTransforms(MyGumball, nonNullGeom.ToArray(), Nothing)
            End If
        End If

        'Create Gumball class for non-null geometry only (no empty GhGumball array).
        If nonNullGeom.Count = 0 Then
            If (MyGumball IsNot Nothing) Then
                If ProximityCache Then
                    GhGumball.RememberShiftedTransformsOnClear(Me, MyGumball, Cache)
                End If
                MyGumball.Dispose()
                MyGumball = Nothing
            End If
        ElseIf (MyGumball Is Nothing) Then
            MyGumball = New GhGumball(nonNullGeom.ToArray(), Me)
            SyncGumballVisibility()
            If ProximityCache AndAlso ShiftedGumballEntries.Count > 0 Then
                ApplyShiftedGumballTransforms(MyGumball, nonNullGeom.ToArray(), Nothing)
            End If
        End If

        ApplyAlignGeometryInput(DA, InputData)
        RefreshSnapTranslateTargets(DA, InputData)
        BuildSlotSettings(DA, InputData)
        ApplyRelocateGeometryInput(DA, InputData)
        ApplyPerSlotDisplayModes()

        'Set output data (null leaves pass through unchanged on both outputs).
        Dim DataOutput As New GH_Structure(Of Types.IGH_GeometricGoo)
        Dim DataOutput2 As New GH_Structure(Of Types.GH_Transform)
        If (Paths IsNot Nothing AndAlso Paths.Length > 0 AndAlso _leafToGumballSlot IsNot Nothing) Then
            For leaf As Integer = 0 To Paths.Length - 1
                Dim slot As Integer = _leafToGumballSlot(leaf)
                If slot < 0 OrElse MyGumball Is Nothing Then
                    DataOutput.Append(Nothing, Paths(leaf))
                    DataOutput2.Append(Nothing, Paths(leaf))
                Else
                    Dim d As Types.IGH_GeometricGoo = Nothing
                    Dim xfOut As Grasshopper.Kernel.Types.GH_Transform = Nothing
                    If LiveTransformsWhileDragging AndAlso MyGumball.PreviewGripSlot >= 0 AndAlso Not (MyGumball.PreviewGripDelta = Transform.Identity) Then

                        Dim pSlot As Integer = MyGumball.PreviewGripSlot
                        Dim liveD As Transform = MyGumball.PreviewGripDelta

                        Select Case EffectiveTransformMode(pSlot)

                            Case 1 ' Apply to all — preview transforms slots in the same branch/group.
                                If IsTransformGroupMember(slot, pSlot) Then
                                    Dim gx As GeometryBase = MyGumball.Geometry(slot).Duplicate()
                                    gx.Transform(liveD)
                                    d = GH_Convert.ToGeometricGoo(gx)
                                    xfOut = ComposeGhTransformAppendGeneric(MyGumball.Xform(slot), liveD)
                                End If

                            Case 0 ' Normal — preview only on the dragged slot.
                                If slot = pSlot Then
                                    Dim gx0 As GeometryBase = MyGumball.Geometry(slot).Duplicate()
                                    gx0.Transform(liveD)
                                    d = GH_Convert.ToGeometricGoo(gx0)
                                    xfOut = ComposeGhTransformAppendGeneric(MyGumball.Xform(slot), liveD)
                                End If

                            Case Else ' Relocate: geometry/transform outputs unchanged; Expire refreshes dependents.

                        End Select
                    End If

                    If d Is Nothing Then
                        d = GH_Convert.ToGeometricGoo(MyGumball.Geometry(slot))
                    End If
                    If xfOut Is Nothing Then
                        xfOut = MyGumball.Xform(slot)
                    End If
                    DataOutput.Append(d, Paths(leaf))
                    DataOutput2.Append(xfOut, Paths(leaf))
                End If
            Next
        End If

        DA.SetDataTree(0, DataOutput)
        DA.SetDataTree(1, DataOutput2)

        SyncGumballVisibility()
    End Sub

    Private Shared Function ComposeGhTransformAppendGeneric(baseXf As Grasshopper.Kernel.Types.GH_Transform, generic As Transform) As Grasshopper.Kernel.Types.GH_Transform
        Dim result As New Grasshopper.Kernel.Types.GH_Transform()
        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In baseXf.CompoundTransforms
            result.CompoundTransforms.Add(t.Duplicate())
        Next
        Dim append As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(generic))
        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In append.CompoundTransforms
            result.CompoundTransforms.Add(t.Duplicate())
        Next
        result.ClearCaches()
        Return result
    End Function

    Private Sub SetCache(_InputData As DataTree(Of GeometryBase))

        Cache.Clear()
        If (_InputData.DataCount = 0) Then
            Paths = New GH_Path(-1) {}
            Exit Sub
        End If

        Paths = New GH_Path(_InputData.DataCount - 1) {}
        Dim count As New Integer
        For i As Int32 = 0 To _InputData.BranchCount - 1
            For j As Int32 = 0 To _InputData.Branch(i).Count - 1
                Dim cell As GeometryBase = _InputData.Branch(i)(j)
                If cell Is Nothing Then
                    Cache.Add(Nothing, _InputData.Path(i))
                Else
                    Cache.Add(cell.Duplicate(), _InputData.Path(i))
                End If
                Paths(count) = _InputData.Path(i)
                count += 1
            Next
        Next
    End Sub

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

    Private Sub StoreGumballTreeKeys(paths As List(Of GH_Path), branch As List(Of Integer))
        CacheTreeKeys = BuildTreeKeys(paths, branch)
    End Sub

    Private Function ShouldDeferGumballResync() As Boolean
        Return MyGumball IsNot Nothing AndAlso MyGumball.IsGripInteractionActive()
    End Function

    Friend Shared Function TransformIsSignificant(xf As Transform) As Boolean
        Return xf <> Transform.Identity
    End Function

    ''' <summary>Tolerant curve comparison for upstream cache checks (avoids spurious resync from float noise).</summary>
    Private Shared Function CurvesGeomEqual(a As Curve, b As Curve) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return a Is Nothing AndAlso b Is Nothing
        If Not a.IsValid OrElse Not b.IsValid Then Return False
        Const tol As Double = 0.0001
        If a.Degree <> b.Degree Then Return False
        If a.IsClosed <> b.IsClosed Then Return False
        If a.IsPeriodic <> b.IsPeriodic Then Return False
        If Math.Abs(a.GetLength() - b.GetLength()) > tol Then Return False
        If Math.Abs(a.Domain.T0 - b.Domain.T0) > tol OrElse Math.Abs(a.Domain.T1 - b.Domain.T1) > tol Then Return False
        If a.PointAtStart.DistanceTo(b.PointAtStart) > tol Then Return False
        If a.PointAtEnd.DistanceTo(b.PointAtEnd) > tol Then Return False
        For k As Integer = 0 To 8
            Dim t As Double = k / 8.0R
            Dim pa As Point3d = a.PointAt(a.Domain.ParameterAt(t))
            Dim pb As Point3d = b.PointAt(b.Domain.ParameterAt(t))
            If pa.DistanceTo(pb) > tol Then Return False
        Next
        Return True
    End Function

    Private Function AreEquals(ByVal A As DataTree(Of GeometryBase), ByVal B As DataTree(Of GeometryBase)) As Boolean
        If (A.DataCount <> B.DataCount) Then
            ' Rhino.RhinoApp.WriteLine("Distinto DataCount")
            Return False
            Exit Function
        End If
        If (A.BranchCount <> B.BranchCount) Then
            ' Rhino.RhinoApp.WriteLine("Distinto BranchCount")
            Return False
            Exit Function
        End If
        For br As Int32 = 0 To A.BranchCount - 1
            For i As Int32 = 0 To A.Branch(br).Count - 1
                If (A.Path(br) <> B.Path(br)) Then
                    ' Rhino.RhinoApp.WriteLine("Distinto Path")
                    Return False
                    Exit Function
                End If
                Dim aCell As GeometryBase = A.Branch(br)(i)
                Dim bCell As GeometryBase = B.Branch(br)(i)
                If aCell Is Nothing Then
                    If bCell Is Nothing Then Continue For
                    Return False
                    Exit Function
                End If
                If bCell Is Nothing Then Return False : Exit Function
                If (aCell.ObjectType <> bCell.ObjectType) Then
                    ' Rhino.RhinoApp.WriteLine("Distinto ObjectType")
                    Return False
                    Exit Function
                End If
                Select Case aCell.ObjectType
                    Case Rhino.DocObjects.ObjectType.Point
                        Dim ptA As Rhino.Geometry.Point = DirectCast(aCell, Rhino.Geometry.Point)
                        Dim ptB As Rhino.Geometry.Point = DirectCast(bCell, Rhino.Geometry.Point)
                        If (New Point3d(Math.Round(ptA.Location.X, 4), Math.Round(ptA.Location.Y, 4), Math.Round(ptA.Location.Z, 4)) <>
            New Point3d(Math.Round(ptB.Location.X, 4), Math.Round(ptB.Location.Y, 4), Math.Round(ptB.Location.Z, 4))) Then
                            Return False
                            Exit Function
                        End If
                    Case Rhino.DocObjects.ObjectType.Curve
                        If Not CurvesGeomEqual(DirectCast(aCell, Curve), DirectCast(bCell, Curve)) Then
                            Return False
                            Exit Function
                        End If

                    Case Rhino.DocObjects.ObjectType.Brep
                        Dim BrpA As Brep = DirectCast(aCell, Brep)
                        Dim BrpB As Brep = DirectCast(bCell, Brep)
                        If (BrpA.Vertices.Count <> BrpB.Vertices.Count) Then
                            '     Rhino.RhinoApp.WriteLine("Distinto VerticesCount")
                            Return False
                            Exit Function
                        End If
                        If (BrpA.Surfaces.Count <> BrpB.Surfaces.Count) Then
                            '    Rhino.RhinoApp.WriteLine("Distinto SrfCount")
                            Return False
                            Exit Function
                        End If
                        If (BrpA.Edges.Count <> BrpB.Edges.Count) Then
                            '   Rhino.RhinoApp.WriteLine("Distinto EdgeCount")
                            Return False
                            Exit Function
                        End If
                        For j As Int32 = 0 To BrpA.Vertices.Count - 1
                            If (New Point3d(Math.Round(BrpA.Vertices(j).Location.X, 4), Math.Round(BrpA.Vertices(j).Location.Y, 4), Math.Round(BrpA.Vertices(j).Location.Z, 4)) <>
              New Point3d(Math.Round(BrpB.Vertices(j).Location.X, 4), Math.Round(BrpB.Vertices(j).Location.Y, 4), Math.Round(BrpB.Vertices(j).Location.Z, 4))) Then
                                '     Rhino.RhinoApp.WriteLine("Distinto Vertices")
                                Return False
                                Exit Function
                            End If
                        Next
                    Case Rhino.DocObjects.ObjectType.Mesh
                        Dim mshA As Mesh = DirectCast(aCell, Mesh)
                        Dim mshB As Mesh = DirectCast(bCell, Mesh)
                        If (mshA.Vertices.Count <> mshB.Vertices.Count) Then
                            Return False
                            Exit Function
                        End If
                        If (mshA.Faces.Count <> mshB.Faces.Count) Then
                            Return False
                            Exit Function
                        End If
                        For j As Int32 = 0 To mshA.Vertices.Count - 1
                            If (New Point3d(Math.Round(mshA.Vertices(j).X, 4), Math.Round(mshA.Vertices(j).Y, 4), Math.Round(mshA.Vertices(j).Z, 4)) <>
              New Point3d(Math.Round(mshB.Vertices(j).X, 4), Math.Round(mshB.Vertices(j).Y, 4), Math.Round(mshB.Vertices(j).Z, 4))) Then
                                Return False
                                Exit Function
                            End If
                        Next

                    Case Else
                        Dim boxA As BoundingBox = aCell.GetBoundingBox(True)
                        Dim boxB As BoundingBox = bCell.GetBoundingBox(True)
                        If Not boxA.IsValid OrElse Not boxB.IsValid Then
                            Return False
                            Exit Function
                        End If
                        Const bboxCompareTol As Double = 0.001
                        If boxA.Min.DistanceTo(boxB.Min) > bboxCompareTol OrElse boxA.Max.DistanceTo(boxB.Max) > bboxCompareTol Then
                            Return False
                            Exit Function
                        End If

                End Select
            Next
        Next
        Return True
    End Function

#Region "Write/Read"

    Public Overrides Function Write(ByVal writer As GH_IO.Serialization.GH_IWriter) As Boolean
        If MyGumball IsNot Nothing AndAlso MyGumball.IsRuntimeStateCompleteForSerialization() Then
            MyGumball.GumballWriter(writer)
        End If
        writer.SetBoolean("GB_SaveShifted", Me.ProximityCache)
        writer.SetInt32("GB_ShiftedCount", ShiftedGumballEntries.Count)
        For i As Integer = 0 To ShiftedGumballEntries.Count - 1
            Dim entry As ShiftedGumballEntry = ShiftedGumballEntries(i)
            writer.SetInt32("GB_ShiftedType", i, entry.Key.ObjectType)
            writer.SetDouble("GB_ShiftedCx", i, entry.Key.Center.X)
            writer.SetDouble("GB_ShiftedCy", i, entry.Key.Center.Y)
            writer.SetDouble("GB_ShiftedCz", i, entry.Key.Center.Z)
            writer.SetDouble("GB_ShiftedDiag", i, entry.Key.Diagonal)
            Dim xfChunk As GH_IO.Serialization.GH_IWriter = writer.CreateChunk("GB_ShiftedXf", i)
            entry.Xform.Write(xfChunk)
        Next
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(ByVal reader As GH_IO.Serialization.GH_IReader) As Boolean

        Dim discardSs As Boolean = False
        reader.TryGetBoolean("GB_SaveShifted", discardSs)
        ' Save-shifted always mirrors proximity; force after proximity flag is known from attributes/deserialize.
        Me.SaveShifted = Me.ProximityCache
        ShiftedGumballEntries.Clear()
        Dim shiftedCount As Integer = 0
        If reader.TryGetInt32("GB_ShiftedCount", shiftedCount) AndAlso shiftedCount > 0 Then
            For i As Integer = 0 To shiftedCount - 1
                Dim entry As New ShiftedGumballEntry
                entry.Key.ObjectType = reader.GetInt32("GB_ShiftedType", i)
                entry.Key.Center = New Point3d(
                    reader.GetDouble("GB_ShiftedCx", i),
                    reader.GetDouble("GB_ShiftedCy", i),
                    reader.GetDouble("GB_ShiftedCz", i))
                entry.Key.Diagonal = reader.GetDouble("GB_ShiftedDiag", i)
                Dim xfChunk As GH_IO.Serialization.GH_IReader = reader.FindChunk("GB_ShiftedXf", i)
                If xfChunk IsNot Nothing Then
                    entry.Xform = New Types.GH_Transform()
                    entry.Xform.Read(xfChunk)
                Else
                    entry.Xform = New Types.GH_Transform()
                End If
                If entry.Key.Center.IsValid Then ShiftedGumballEntries.Add(entry)
            Next
        End If

        If Not GhGumball.GhClipboardRootLooksComplete(reader) Then
            Return MyBase.Read(reader)
        End If

        If MyGumball Is Nothing Then
            Dim candidate As New GhGumball(reader, Me)
            If candidate.IsRuntimeStateCompleteForSerialization() Then
                MyGumball = candidate
            Else
                candidate.ClearAfterFailedOrEmptyDeserialize()
            End If
        Else
            MyGumball.HideGumballs()
            If Not MyGumball.GumballReader(reader) Then
                Dim d As GhGumball = MyGumball
                MyGumball = Nothing
                Try
                    d.Dispose()
                Catch
                End Try
            End If
        End If

        Return MyBase.Read(reader)
    End Function
#End Region

#Region "Preview"

    Private Function GumballPreviewGeometry(slot As Integer) As GeometryBase
        If MyGumball Is Nothing OrElse slot < 0 OrElse slot >= MyGumball.Count Then Return Nothing
        Dim src As GeometryBase = MyGumball.Geometry(slot)
        If src Is Nothing Then Return Nothing

        If LiveTransformsWhileDragging AndAlso MyGumball.PreviewGripSlot >= 0 AndAlso
            Not (MyGumball.PreviewGripDelta = Transform.Identity) Then
            Dim pSlot As Integer = MyGumball.PreviewGripSlot
            Dim liveD As Transform = MyGumball.PreviewGripDelta
            Select Case EffectiveTransformMode(pSlot)
                Case 1
                    If IsTransformGroupMember(slot, pSlot) Then
                        Dim gx As GeometryBase = src.Duplicate()
                        gx.Transform(liveD)
                        Return gx
                    End If
                Case 0
                    If slot = pSlot Then
                        Dim gx As GeometryBase = src.Duplicate()
                        gx.Transform(liveD)
                        Return gx
                    End If
            End Select
        End If
        Return src.Duplicate()
    End Function

    Private Sub DrawGumballHiddenPreviewGeometry(args As IGH_PreviewArgs, drawMeshes As Boolean)
        If MyGumball Is Nothing OrElse MyGumball.Count = 0 OrElse args Is Nothing OrElse args.Display Is Nothing Then Return
        Dim selected As Boolean = Attributes IsNot Nothing AndAlso Attributes.Selected
        Dim wireCol As Color = If(selected, args.WireColour_Selected, args.WireColour)
        Dim meshMat As DisplayMaterial = If(selected, args.ShadeMaterial_Selected, args.ShadeMaterial)
        Dim thick As Integer = If(selected, 2, 1)

        For i As Integer = 0 To MyGumball.Count - 1
            If SlotSettings IsNot Nothing AndAlso i < SlotSettings.Length AndAlso Not SlotSettings(i).Active Then Continue For
            Dim g As GeometryBase = GumballPreviewGeometry(i)
            If g Is Nothing Then Continue For
            Try
                If drawMeshes Then
                    DrawGumballPreviewMeshes(args.Display, g, meshMat)
                Else
                    DrawGumballPreviewWires(args.Display, g, wireCol, thick)
                End If
            Finally
                g.Dispose()
            End Try
        Next
    End Sub

    Private Shared Sub DrawGumballPreviewWires(display As DisplayPipeline, geom As GeometryBase, col As Color, thickness As Integer)
        If geom Is Nothing OrElse display Is Nothing Then Return

        Dim pt As Rhino.Geometry.Point = TryCast(geom, Rhino.Geometry.Point)
        If pt IsNot Nothing Then
            display.DrawPoint(pt.Location, PointStyle.RoundSimple, 6, col)
            Return
        End If

        Dim crv As Curve = TryCast(geom, Curve)
        If crv IsNot Nothing Then
            display.DrawCurve(crv, col, thickness)
            Return
        End If

        Dim brep As Brep = TryCast(geom, Brep)
        If brep IsNot Nothing Then
            display.DrawBrepWires(brep, col, thickness)
            Return
        End If

        Dim ext As Extrusion = TryCast(geom, Extrusion)
        If ext IsNot Nothing Then
            Dim tb As Brep = ext.ToBrep()
            If tb IsNot Nothing Then
                Try
                    display.DrawBrepWires(tb, col, thickness)
                Finally
                    tb.Dispose()
                End Try
            End If
            Return
        End If

        Dim mesh As Mesh = TryCast(geom, Mesh)
        If mesh IsNot Nothing Then
            display.DrawMeshWires(mesh, col)
            Return
        End If

        Dim srf As Surface = TryCast(geom, Surface)
        If srf IsNot Nothing Then
            display.DrawSurface(srf, col, 8)
            Return
        End If

        Dim subd As SubD = TryCast(geom, SubD)
        If subd IsNot Nothing Then
            Dim sm As Mesh = Mesh.CreateFromSubD(subd, 2)
            If sm IsNot Nothing Then
                Try
                    display.DrawMeshWires(sm, col)
                Finally
                    sm.Dispose()
                End Try
            End If
            Return
        End If

        Dim bb As BoundingBox = geom.GetBoundingBox(True)
        If bb.IsValid Then display.DrawBox(bb, col, thickness)
    End Sub

    Private Shared Sub DrawGumballPreviewMeshes(display As DisplayPipeline, geom As GeometryBase, mat As DisplayMaterial)
        If geom Is Nothing OrElse display Is Nothing OrElse mat Is Nothing Then Return

        Dim brep As Brep = TryCast(geom, Brep)
        If brep IsNot Nothing Then
            display.DrawBrepShaded(brep, mat)
            Return
        End If

        Dim ext As Extrusion = TryCast(geom, Extrusion)
        If ext IsNot Nothing Then
            Dim tb As Brep = ext.ToBrep()
            If tb IsNot Nothing Then
                Try
                    display.DrawBrepShaded(tb, mat)
                Finally
                    tb.Dispose()
                End Try
            End If
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
    End Sub

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        SyncGumballVisibility(suppressRedraw:=True)
        If Me.Hidden Then
            If ViewportPreview.IsEffectivelyPreviewed(Me) Then
                DrawGumballHiddenPreviewGeometry(args, drawMeshes:=False)
            End If
        Else
            MyBase.DrawViewportWires(args)
        End If
    End Sub

    Public Overrides Sub DrawViewportMeshes(args As IGH_PreviewArgs)
        If Me.Hidden Then
            If ViewportPreview.IsEffectivelyPreviewed(Me) Then
                DrawGumballHiddenPreviewGeometry(args, drawMeshes:=True)
            End If
        Else
            MyBase.DrawViewportMeshes(args)
        End If
    End Sub

#End Region

End Class

''' <summary>Gumball transform saved when its geometry leaves the input list (Save shifted).</summary>
Friend Structure ShiftedGumballEntry
    Public Key As GeometryProximityKey
    Public Xform As Types.GH_Transform
End Structure

Public Class GumballCompAtt
    Inherits Grasshopper.Kernel.Attributes.GH_ComponentAttributes

    Private MyOwner As GumballComp

    Sub New(owner As GumballComp)
        MyBase.New(owner)
        MyOwner = owner
    End Sub

    Public Overrides Property Selected As Boolean
        Get
            Return MyBase.Selected
        End Get

        Set(value As Boolean)

            MyBase.Selected = value
            MyOwner.SyncGumballVisibility()
        End Set
    End Property

End Class

Public Class GhGumball
    Inherits Rhino.UI.MouseCallback
    Implements GH_ISerializable

    Public Geometry As GeometryBase()
    Public Xform As Grasshopper.Kernel.Types.GH_Transform()

    Public Conduits As Rhino.UI.Gumball.GumballDisplayConduit()
    Private Gumballs As Rhino.UI.Gumball.GumballObject()
    Private Appearances As Rhino.UI.Gumball.GumballAppearanceSettings()
    Private MyCustomAppearance As Integer() = New Integer(9) {1, 1, 2, 1, 1, 50, 5, 2, 15, 35}

    Public Count As Integer
    Public Component As GumballComp
    Public GeometrytoAlign As GeometryBase
    ''' <summary>Per-slot align state (plane axis or geometry).</summary>
    Private Enum SlotAlignKind
        None = 0
        Axis = 1
        Geometry = 2
    End Enum

    Private Structure SlotAlignState
        Public Kind As SlotAlignKind
        Public AxisPlane As Plane
        Public AlignGeo As GeometryBase
    End Structure

    Private SlotAlignStates As SlotAlignState()
    ''' <summary>When align is on and input A is a Plane, gumball axes match this frame; each gumball keeps its own origin.</summary>
    Public AlignAxisReferencePlane As Plane
    Public HasAlignAxisReferencePlane As Boolean
    ''' <remarks>Listening only between grip mouse-down (non-numeric pick) and mouse-up so Esc cancels preview.</remarks>
    Private _rhinoEscapeSubscribed As Boolean
    ''' <remarks>PreTransform at grip pick (before mouse-move updates); restored on Escape so the gumball does not reset to identity or desync from committed geometry.</remarks>
    Private _dragPreTransformSnapshot As Transform
    Private _dragPreTransformCaptured As Boolean
    ''' <summary>While <see cref="GumballComp.LiveTransformsWhileDragging"/> and a grip drag is active: pending conduit delta not yet committed to stored geometry / compound transforms.</summary>
    Friend PreviewGripSlot As Integer = -1
    Friend PreviewGripDelta As Transform = Transform.Identity
    ''' <summary>Per-slot snap targets from optional input S (tree matched to geometry paths).</summary>
    Friend SlotSnapTranslateTargets As List(Of GeometryBase)()
    ''' <summary>Positive: max snap distance from Attributes snap tol (model units); NaN: screen-pixel default.</summary>
    Friend SnapTranslateRadiusOverride As Double = Double.NaN

    Friend Function SnapTargetsForSlot(slot As Integer) As List(Of GeometryBase)
        If SlotSnapTranslateTargets Is Nothing OrElse slot < 0 OrElse slot >= SlotSnapTranslateTargets.Length Then Return Nothing
        Return SlotSnapTranslateTargets(slot)
    End Function

    Friend Sub DisposeSnapTranslateTargets()
        If SlotSnapTranslateTargets Is Nothing Then Return
        For Each lst As List(Of GeometryBase) In SlotSnapTranslateTargets
            If lst Is Nothing Then Continue For
            For Each g As GeometryBase In lst
                Try
                    g.Dispose()
                Catch
                End Try
            Next
        Next
        SlotSnapTranslateTargets = Nothing
    End Sub

    ''' <summary>Gumball is safe to clipboard-serialize only when SolveInstance built all parallel arrays consistently.</summary>
    Friend Function IsRuntimeStateCompleteForSerialization() As Boolean
        If Count <= 0 Then Return False
        If Geometry Is Nothing OrElse Xform Is Nothing OrElse Gumballs Is Nothing OrElse Conduits Is Nothing OrElse Appearances Is Nothing Then Return False
        If Geometry.Length <> Count OrElse Xform.Length <> Count OrElse Gumballs.Length <> Count OrElse Conduits.Length <> Count OrElse Appearances.Length <> Count Then Return False
        Return True
    End Function

    Friend Shared Function GhClipboardRootLooksComplete(reader As GH_IO.Serialization.GH_IReader) As Boolean
        If reader Is Nothing OrElse Not reader.ChunkExists("gbroot") Then Return False
        Try
            Dim root As GH_IO.Serialization.GH_IReader = reader.FindChunk("gbroot")
            If root Is Nothing Then Return False
            Dim data As GH_IO.Serialization.GH_IReader = root.FindChunk("gbdata", 0)
            If data Is Nothing Then Return False
            Dim countgeo As GH_IO.Serialization.GH_IReader = data.FindChunk("countgeo", 0)
            If countgeo Is Nothing Then Return False
            Dim ct As Integer = countgeo.GetInt32("count", 0)
            If ct <= 0 Then Return False
            Dim geoCh As GH_IO.Serialization.GH_IReader = data.FindChunk("geometry", 1)
            Dim xfCh As GH_IO.Serialization.GH_IReader = data.FindChunk("transform", 2)
            Dim gumCh As GH_IO.Serialization.GH_IReader = data.FindChunk("gumball", 3)
            If geoCh Is Nothing OrElse xfCh Is Nothing OrElse gumCh Is Nothing Then Return False
            Dim att As GH_IO.Serialization.GH_IReader = root.FindChunk("gbattributes", 1)
            If att Is Nothing Then Return False
            Dim att0 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_values", 0)
            Dim att1 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_modes", 1)
            If att0 Is Nothing OrElse att1 Is Nothing Then Return False
            Return True
        Catch
            Return False
        End Try
    End Function

    Friend Sub ClearAfterFailedOrEmptyDeserialize()
        If Conduits IsNot Nothing Then
            For ix As Integer = 0 To Conduits.Length - 1
                Try
                    If Conduits(ix) IsNot Nothing Then
                        Conduits(ix).Enabled = False
                        Conduits(ix).Dispose()
                    End If
                Catch
                End Try
            Next
        End If
        If Gumballs IsNot Nothing Then
            For ix As Integer = 0 To Gumballs.Length - 1
                Try
                    If Gumballs(ix) IsNot Nothing Then Gumballs(ix).Dispose()
                Catch
                End Try
            Next
        End If
        If Geometry IsNot Nothing Then
            For ix As Integer = 0 To Geometry.Length - 1
                Try
                    Dim g As GeometryBase = Geometry(ix)
                    If g IsNot Nothing Then g.Dispose()
                Catch
                End Try
            Next
        End If
        Count = 0
        Geometry = Nothing
        Xform = Nothing
        Gumballs = Nothing
        Conduits = Nothing
        Appearances = Nothing
        GeometrytoAlign = Nothing
        HasAlignAxisReferencePlane = False
        AlignAxisReferencePlane = Plane.Unset
        If SlotAlignStates IsNot Nothing Then
            For i As Integer = 0 To SlotAlignStates.Length - 1
                ClearSlotAlignStateOnly(i)
            Next
        End If
        SlotAlignStates = Nothing
    End Sub

#Region "New/Show/Hide/Dispose"

    Sub New(Geo As GeometryBase(), comp As GumballComp)
        Component = comp
        Geometry = Geo
        Count = Geo.Count
        Xform = New Grasshopper.Kernel.Types.GH_Transform(Count - 1) {}
        Conduits = New Rhino.UI.Gumball.GumballDisplayConduit(Count - 1) {}
        Gumballs = New Rhino.UI.Gumball.GumballObject(Count - 1) {}
        Appearances = New Rhino.UI.Gumball.GumballAppearanceSettings(Count - 1) {}
        ReDim SlotAlignStates(Count - 1)
        ReDim SlotSnapTranslateTargets(Count - 1)

        For i As Int32 = 0 To Count - 1
            Xform(i) = New Grasshopper.Kernel.Types.GH_Transform()

            'Appearance.
            Dim app As New Rhino.UI.Gumball.GumballAppearanceSettings
            app.MenuEnabled = False

            'Translate.
            app.TranslateXEnabled = MyCustomAppearance(0)
            app.TranslateYEnabled = MyCustomAppearance(0)
            app.TranslateZEnabled = MyCustomAppearance(0)
            'Free translate.
            If (MyCustomAppearance(2)) Then
                app.FreeTranslate = 2
            Else
                app.FreeTranslate = 0
            End If
            'Rotate.
            app.RotateXEnabled = MyCustomAppearance(3)
            app.RotateYEnabled = MyCustomAppearance(3)
            app.RotateZEnabled = MyCustomAppearance(3)
            'Scale.
            app.ScaleXEnabled = MyCustomAppearance(4)
            app.ScaleYEnabled = MyCustomAppearance(4)
            app.ScaleZEnabled = MyCustomAppearance(4)
            'Radius.
            app.Radius = MyCustomAppearance(5)
            'Head.
            app.ArrowHeadLength = MyCustomAppearance(6) * 2
            app.ArrowHeadWidth = MyCustomAppearance(6)
            'Thickness.
            app.AxisThickness = MyCustomAppearance(7)
            app.ArcThickness = MyCustomAppearance(7)
            'Planar translate.
            If MyCustomAppearance(1) Then
                app.TranslateXYEnabled = True
                app.TranslateYZEnabled = True
                app.TranslateZXEnabled = True
                'Plane size.
                app.PlanarTranslationGripSize = MyCustomAppearance(8)
                'Plane distance.
                app.PlanarTranslationGripCorner = MyCustomAppearance(9)
            Else
                app.TranslateXYEnabled = False
                app.TranslateYZEnabled = False
                app.TranslateZXEnabled = False
                'Plane size.
                app.PlanarTranslationGripSize = 0
                'Plane distance.
                app.PlanarTranslationGripCorner = 0
            End If

            If (Geometry(i).ObjectType = Rhino.DocObjects.ObjectType.Point) Then
                app.ScaleXEnabled = False
                app.ScaleYEnabled = False
                app.ScaleZEnabled = False
            End If

            Appearances(i) = app

            'Gumball object.
            Dim GumBall As New Rhino.UI.Gumball.GumballObject
            If (Geo(i).ObjectType = Rhino.DocObjects.ObjectType.Point) Then
                Dim pt As Rhino.Geometry.Point = DirectCast(Geo(i), Rhino.Geometry.Point)
                GumBall.SetFromPlane(New Plane(pt.Location, Vector3d.XAxis, Vector3d.YAxis))
            ElseIf (Geo(i).ObjectType = Rhino.DocObjects.ObjectType.Curve) Then
                Dim crv As Rhino.Geometry.Curve = DirectCast(Geo(i), Rhino.Geometry.Curve)
                GumBall.SetFromCurve(crv)
            Else
                GumBall.SetFromBoundingBox(Geo(i).GetBoundingBox(True))
            End If
            Me.Gumballs(i) = GumBall

            'Display conduit.
            Dim conduit As New Rhino.UI.Gumball.GumballDisplayConduit
            conduit.SetBaseGumball(GumBall, app)
            Me.Conduits(i) = conduit
        Next

    End Sub

    ''' <summary>
    ''' After upstream geometry edits, rebuild a gumball for <paramref name="newGeoms"/> and carry over compound transforms where object types align by index.
    ''' </summary>
    Friend Shared Function CreateResyncPreservingTransforms(newGeoms As GeometryBase(), comp As GumballComp, oldGb As GhGumball) As GhGumball
        Return CreateResyncPreservingTransformsWithMap(newGeoms, comp, oldGb, BuildIndexSlotMap(newGeoms.Length, oldGb.Count))
    End Function

    ''' <summary>
    ''' Like <see cref="CreateResyncPreservingTransforms"/>, but each new slot takes transforms from the prior gumball entry whose cached upstream bbox centre is nearest (same object type), within proximity tolerance.
    ''' </summary>
    Friend Shared Function CreateResyncPreservingTransformsProximity(newGeoms As GeometryBase(), comp As GumballComp, oldGb As GhGumball, oldCache As DataTree(Of GeometryBase),
                                                                     newPaths As List(Of GH_Path), newBranch As List(Of Integer),
                                                                     Optional preferIndexIfIdentity As Boolean = False) As GhGumball
        Dim oldPaths As New List(Of GH_Path)
        Dim oldBranch As New List(Of Integer)
        ExtractSlotPathsFromCacheForResync(oldCache, oldPaths, oldBranch)
        Dim oldCacheGeoms As GeometryBase() = ExtractCacheGeomsForResync(oldCache)
        Dim slotMap As Integer() = ProximityMatching.BuildTransformSlotMap(oldCacheGeoms, newGeoms, oldPaths, oldBranch, newPaths, newBranch, requireMatchingPaths:=False)
        ' Mixed cache: same tree keys + identity proximity map → keep by index (far moves). Wrap-shift is not identity.
        If preferIndexIfIdentity AndAlso ProximityMatching.SlotMapIsIndexIdentity(slotMap) Then
            Return CreateResyncPreservingTransforms(newGeoms, comp, oldGb)
        End If
        ' Save-shifted is always on with proximity cache.
        comp.RememberShiftedGumballTransforms(oldGb, oldCacheGeoms, newGeoms, slotMap)
        Dim gb As GhGumball = CreateResyncPreservingTransformsWithMap(newGeoms, comp, oldGb, slotMap)
        comp.ApplyShiftedGumballTransforms(gb, newGeoms, slotMap)
        Return gb
    End Function

    Private Shared Sub ExtractSlotPathsFromCacheForResync(tree As DataTree(Of GeometryBase), paths As List(Of GH_Path), branch As List(Of Integer))
        paths.Clear()
        branch.Clear()
        If tree Is Nothing Then Return
        For bi As Integer = 0 To tree.BranchCount - 1
            Dim path As GH_Path = tree.Paths(bi)
            For j As Integer = 0 To tree.Branch(bi).Count - 1
                If tree.Branch(bi)(j) IsNot Nothing Then
                    paths.Add(path)
                    branch.Add(j)
                End If
            Next
        Next
    End Sub

    ''' <summary>
    ''' When upstream geometry is cleared (all null / empty), remember gumball transforms before dispose so they can be restored when geometry returns.
    ''' </summary>
    Friend Shared Sub RememberShiftedTransformsOnClear(comp As GumballComp, oldGb As GhGumball, oldCache As DataTree(Of GeometryBase))
        If comp Is Nothing OrElse Not comp.ProximityCache OrElse oldGb Is Nothing Then Return
        Dim oldCacheGeoms As GeometryBase() = ExtractCacheGeomsForResync(oldCache)
        If oldCacheGeoms.Length = 0 Then Return
        comp.RememberShiftedGumballTransforms(oldGb, oldCacheGeoms, Array.Empty(Of GeometryBase)(), Nothing)
    End Sub

    ''' <summary>Non-null cached upstream geometry per gumball slot (same order as <see cref="ExtractSlotPathsFromCacheForResync"/>).</summary>
    Private Shared Function ExtractCacheGeomsForResync(tree As DataTree(Of GeometryBase)) As GeometryBase()
        Dim geoms As New List(Of GeometryBase)
        If tree Is Nothing Then Return geoms.ToArray()
        For bi As Integer = 0 To tree.BranchCount - 1
            For j As Integer = 0 To tree.Branch(bi).Count - 1
                Dim g As GeometryBase = tree.Branch(bi)(j)
                If g IsNot Nothing Then geoms.Add(g)
            Next
        Next
        Return geoms.ToArray()
    End Function

    Private Shared Function BuildIndexSlotMap(newCount As Integer, oldCount As Integer) As Integer()
        Dim map(newCount - 1) As Integer
        For j As Integer = 0 To newCount - 1
            map(j) = If(j < oldCount, j, -1)
        Next
        Return map
    End Function

    Friend Shared Function CreateResyncPreservingTransformsWithMap(newGeoms As GeometryBase(), comp As GumballComp, oldGb As GhGumball, newSlotToOldSlot As Integer()) As GhGumball
        Dim gb As New GhGumball(newGeoms, comp)
        Dim clonedAtt(9) As Integer
        Array.Copy(oldGb.CustomAppearance, clonedAtt, 10)
        gb.CustomAppearance = clonedAtt
        If oldGb.SlotAlignStates IsNot Nothing AndAlso oldGb.SlotAlignStates.Length = oldGb.Count Then
            ReDim gb.SlotAlignStates(gb.Count - 1)
            For j As Integer = 0 To gb.Count - 1
                Dim oldIx As Integer = newSlotToOldSlot(j)
                If oldIx >= 0 AndAlso oldIx < oldGb.SlotAlignStates.Length Then
                    Dim st As SlotAlignState = oldGb.SlotAlignStates(oldIx)
                    If st.AlignGeo IsNot Nothing Then
                        st.AlignGeo = st.AlignGeo.Duplicate()
                    End If
                    gb.SlotAlignStates(j) = st
                End If
            Next
        End If
        gb.GeometrytoAlign = If(oldGb.GeometrytoAlign Is Nothing, Nothing, oldGb.GeometrytoAlign.Duplicate())
        gb.HasAlignAxisReferencePlane = oldGb.HasAlignAxisReferencePlane
        gb.AlignAxisReferencePlane = oldGb.AlignAxisReferencePlane

        Dim n As Integer = Math.Min(gb.Count, newSlotToOldSlot.Length)
        For j As Integer = 0 To n - 1
            Dim oldIx As Integer = newSlotToOldSlot(j)
            If oldIx < 0 OrElse oldIx >= oldGb.Count Then Continue For
            If oldGb.Geometry(oldIx).ObjectType <> gb.Geometry(j).ObjectType Then Continue For

            gb.Xform(j) = New Types.GH_Transform()
            For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In oldGb.Xform(oldIx).CompoundTransforms
                gb.Xform(j).CompoundTransforms.Add(t.Duplicate())
            Next
            gb.Xform(j).ClearCaches()

            ApplyCompoundGenericTransformsInOrder(gb.Geometry(j), gb.Xform(j))
            gb.RebuildGumballObjectAndConduitAt(j)
            gb.Conduits(j).PreTransform = Transform.Identity
        Next

        Return gb
    End Function

    Private Sub SyncCurveGumballBaseAfterCommit(slot As Integer)
        If slot < 0 OrElse slot >= Count Then Return
        If Not TypeOf Geometry(slot) Is Curve Then Return
        RebuildGumballObjectAndConduitAt(slot)
        Conduits(slot).PreTransform = Transform.Identity
    End Sub

    Friend Shared Sub ApplyCompoundGenericTransformsInOrder(geo As GeometryBase, xf As Types.GH_Transform)
        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In xf.CompoundTransforms
            Dim gen = TryCast(t, Grasshopper.Kernel.Types.Transforms.Generic)
            If gen Is Nothing Then Continue For
            geo.Transform(gen.Transform)
        Next
    End Sub

    Friend Sub RebuildGumballObjectAndConduitAt(i As Integer)
        Gumballs(i).Dispose()
        Dim geo As GeometryBase = Geometry(i)
        Dim gumBall As New Rhino.UI.Gumball.GumballObject
        If (geo.ObjectType = Rhino.DocObjects.ObjectType.Point) Then
            Dim pt As Rhino.Geometry.Point = DirectCast(geo, Rhino.Geometry.Point)
            gumBall.SetFromPlane(New Plane(pt.Location, Vector3d.XAxis, Vector3d.YAxis))
        ElseIf (geo.ObjectType = Rhino.DocObjects.ObjectType.Curve) Then
            Dim crv As Rhino.Geometry.Curve = DirectCast(geo, Rhino.Geometry.Curve)
            gumBall.SetFromCurve(crv)
        Else
            gumBall.SetFromBoundingBox(geo.GetBoundingBox(True))
        End If
        Gumballs(i) = gumBall
        Conduits(i).SetBaseGumball(gumBall, Appearances(i))
    End Sub

    Sub New(Reader As GH_IO.Serialization.GH_IReader, comp As GumballComp)

        Component = comp
        Count = 0
        Geometry = Nothing
        Xform = Nothing
        Gumballs = Nothing
        Conduits = Nothing
        Appearances = Nothing

        If Not GhClipboardRootLooksComplete(Reader) Then Exit Sub

        Try
            Dim i As New Integer

            'Root.
            Dim root As GH_IReader = Reader.FindChunk("gbroot")

            'Data.
            Dim data As GH_IReader = root.FindChunk("gbdata", 0)

            'Count.
            Dim countgeo As GH_IReader = data.FindChunk("countgeo", 0)
            Count = countgeo.GetInt32("count", 0)
            ReDim SlotAlignStates(Count - 1)

            'Geomtry.
            Geometry = New GeometryBase(Count - 1) {}
            Dim g As GH_IO.Serialization.GH_IReader = data.FindChunk("geometry", 1)
            For i = 0 To Count - 1
                Dim bytes As Byte() = g.GetByteArray("geo", i)
                Geometry(i) = GH_Convert.ByteArrayToCommonObject(Of GeometryBase)(bytes)
            Next

            'Transform.
            Xform = New Types.GH_Transform(Count - 1) {}
            Dim xf As GH_IO.Serialization.GH_IReader = data.FindChunk("transform", 2)
            For i = 0 To Count - 1
                Dim t As GH_IO.Serialization.GH_IReader = xf.FindChunk("gh_transform", i)
                Dim ghxform As New Types.GH_Transform()
                ghxform.Read(t)
                Xform(i) = ghxform
            Next

            'Gumball.
            Gumballs = New Rhino.UI.Gumball.GumballObject(Count - 1) {}
            Dim go As GH_IO.Serialization.GH_IReader = data.FindChunk("gumball", 3)
            For i = 0 To Count - 1
                Dim gb As New Rhino.UI.Gumball.GumballObject
                Dim frame As New Rhino.UI.Gumball.GumballFrame
                Dim pln As GH_IO.Types.GH_Plane = go.GetPlane("frameplane", i)
                frame.Plane = New Plane(New Point3d(pln.Origin.x, pln.Origin.y, pln.Origin.z), New Vector3d(pln.XAxis.x, pln.XAxis.y, pln.XAxis.z), New Vector3d(pln.YAxis.x, pln.YAxis.y, pln.YAxis.z))
                Dim scd As GH_IO.Types.GH_Point3D = go.GetPoint3D("scalegripdistance", i)
                frame.ScaleGripDistance = New Vector3d(scd.x, scd.y, scd.z)
                gb.Frame = frame
                Gumballs(i) = gb
            Next

            'Attributes.
            Dim att As GH_IReader = root.FindChunk("gbattributes", 1)

            'Attributes_values
            Dim att0 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_values", 0)

            MyCustomAppearance(0) = att0.GetInt32("GbAtt_Translate", 0)
            MyCustomAppearance(1) = att0.GetInt32("GbAtt_PlanarTranslate", 1)
            MyCustomAppearance(2) = att0.GetInt32("GbAtt_FreeTranslate", 2)
            MyCustomAppearance(3) = att0.GetInt32("GbAtt_Rotate", 3)
            MyCustomAppearance(4) = att0.GetInt32("GbAtt_Scale", 4)
            MyCustomAppearance(5) = att0.GetInt32("GbAtt_Radius", 5)
            MyCustomAppearance(6) = att0.GetInt32("GbAtt_ArrowHead", 6)
            MyCustomAppearance(7) = att0.GetInt32("GbAtt_Thickness", 7)
            MyCustomAppearance(8) = att0.GetInt32("GbAtt_PlaneSize", 8)
            MyCustomAppearance(9) = att0.GetInt32("GbAtt_PlaneDistance", 9)


            'Attributes_modes
            Dim att1 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_modes", 1)
            comp.ModeValue(0) = att1.GetInt32("valmode", 0)
            comp.ModeValue(1) = GumballComp.LoadDisplayModeFromChunk(att1)
            Dim alignF As Boolean = att1.GetBoolean("aligntogeometry", 2)
            Dim snapF As Boolean = False
            If Not att1.TryGetBoolean("snaptogeometry", 6, snapF) Then snapF = False
            comp.ApplyOptionalInputModesFromFile(alignF, snapF)
            comp.PreserveTransformsOnGeometryChange = att1.GetBoolean("preservexf", 3)
            Dim proxStored As Boolean
            If att1.TryGetBoolean("proximitycache", 4, proxStored) Then
                comp.ProximityCache = proxStored
            Else
                comp.ProximityCache = False
            End If
            Dim discardSs As Boolean
            att1.TryGetBoolean("saveshifted", 9, discardSs)
            comp.SaveShifted = comp.ProximityCache
            Dim lvStored As Boolean
            If att1.TryGetBoolean("livetransform", 5, lvStored) Then
                comp.LiveTransformsWhileDragging = lvStored
            Else
                comp.LiveTransformsWhileDragging = False
            End If

            'End reader.

            Component = comp
            Conduits = New Rhino.UI.Gumball.GumballDisplayConduit(Count - 1) {}
            Appearances = New Rhino.UI.Gumball.GumballAppearanceSettings(Count - 1) {}
            'MyCallBack = New CustomCallBack(Me)

            For i = 0 To Count - 1

                'Appearance.
                Dim app As New Rhino.UI.Gumball.GumballAppearanceSettings
                app.MenuEnabled = False

                'Translate.
                app.TranslateXEnabled = MyCustomAppearance(0)
                app.TranslateYEnabled = MyCustomAppearance(0)
                app.TranslateZEnabled = MyCustomAppearance(0)
                'Free translate.
                If (MyCustomAppearance(2)) Then
                    app.FreeTranslate = 2
                Else
                    app.FreeTranslate = 0
                End If
                'Rotate.
                app.RotateXEnabled = MyCustomAppearance(3)
                app.RotateYEnabled = MyCustomAppearance(3)
                app.RotateZEnabled = MyCustomAppearance(3)
                'Scale.
                app.ScaleXEnabled = MyCustomAppearance(4)
                app.ScaleYEnabled = MyCustomAppearance(4)
                app.ScaleZEnabled = MyCustomAppearance(4)
                'Radius.
                app.Radius = MyCustomAppearance(5)
                'Head.
                app.ArrowHeadLength = MyCustomAppearance(6) * 2
                app.ArrowHeadWidth = MyCustomAppearance(6)
                'Thickness.
                app.AxisThickness = MyCustomAppearance(7)
                app.ArcThickness = MyCustomAppearance(7)
                'Planar translate.
                If MyCustomAppearance(1) Then
                    app.TranslateXYEnabled = True
                    app.TranslateYZEnabled = True
                    app.TranslateZXEnabled = True
                    'Plane size.
                    app.PlanarTranslationGripSize = MyCustomAppearance(8)
                    'Plane distance.
                    app.PlanarTranslationGripCorner = MyCustomAppearance(9)
                Else
                    app.TranslateXYEnabled = False
                    app.TranslateYZEnabled = False
                    app.TranslateZXEnabled = False
                    'Plane size.
                    app.PlanarTranslationGripSize = 0
                    'Plane distance.
                    app.PlanarTranslationGripCorner = 0
                End If

                If (Geometry(i).ObjectType = Rhino.DocObjects.ObjectType.Point) Then
                    app.ScaleXEnabled = False
                    app.ScaleYEnabled = False
                    app.ScaleZEnabled = False
                End If

                Appearances(i) = app

                'Display conduit.
                Dim conduit As New Rhino.UI.Gumball.GumballDisplayConduit
                conduit.SetBaseGumball(Gumballs(i), app)
                Conduits(i) = conduit
            Next

        Catch ex As Exception
            Rhino.RhinoApp.WriteLine("NEW_GB; " & ex.ToString())
            ClearAfterFailedOrEmptyDeserialize()
        End Try
    End Sub
    '
    '
    Public Sub ShowGumballs()
        If Component IsNot Nothing Then
            Component.SyncGumballVisibility()
            Return
        End If
        If Conduits Is Nothing Then Return
        For i As Int32 = 0 To Conduits.Length - 1
            Conduits(i).Enabled = True
        Next
        Me.Enabled = True
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Friend Sub SetConduitEnabled(slot As Integer, enabled As Boolean)
        If Conduits Is Nothing OrElse slot < 0 OrElse slot >= Count Then Return
        If Conduits(slot).Enabled = enabled Then Return
        Conduits(slot).Enabled = enabled
    End Sub

    Friend Shared Sub ConfigureAppearanceFromPreset(app As Rhino.UI.Gumball.GumballAppearanceSettings, preset As Integer(), geo As GeometryBase)
        app.TranslateXEnabled = preset(0)
        app.TranslateYEnabled = preset(0)
        app.TranslateZEnabled = preset(0)
        If preset(2) <> 0 Then
            app.FreeTranslate = 2
        Else
            app.FreeTranslate = 0
        End If
        app.RotateXEnabled = preset(3)
        app.RotateYEnabled = preset(3)
        app.RotateZEnabled = preset(3)
        app.ScaleXEnabled = preset(4)
        app.ScaleYEnabled = preset(4)
        app.ScaleZEnabled = preset(4)
        app.Radius = preset(5)
        app.ArrowHeadLength = preset(6) * 2
        app.ArrowHeadWidth = preset(6)
        app.AxisThickness = preset(7)
        app.ArcThickness = preset(7)
        If preset(1) <> 0 Then
            app.TranslateXYEnabled = True
            app.TranslateYZEnabled = True
            app.TranslateZXEnabled = True
            app.PlanarTranslationGripSize = preset(8)
            app.PlanarTranslationGripCorner = preset(9)
        Else
            app.TranslateXYEnabled = False
            app.TranslateYZEnabled = False
            app.TranslateZXEnabled = False
            app.PlanarTranslationGripSize = 0
            app.PlanarTranslationGripCorner = 0
        End If
        If geo IsNot Nothing AndAlso geo.ObjectType = Rhino.DocObjects.ObjectType.Point Then
            app.ScaleXEnabled = False
            app.ScaleYEnabled = False
            app.ScaleZEnabled = False
        End If
    End Sub

    Public Sub ApplyAppearancePresetToSlot(slot As Integer, mode As Integer)
        If slot < 0 OrElse slot >= Count OrElse Appearances Is Nothing Then Return
        Dim preset As Integer() = GumballComp.BuildAppearancePresetForAtt(mode, CustomAppearance)
        ConfigureAppearanceFromPreset(Appearances(slot), preset, Geometry(slot))
    End Sub

    ''' <summary>Push updated appearance settings to live gumball conduits (toggle required for Rhino to refresh grips).</summary>
    Friend Sub RefreshConduitDisplays()
        If IsLiveGripDragActive() Then Return
        If IsNumericGripPickActive() Then Return
        If Conduits Is Nothing OrElse Gumballs Is Nothing OrElse Appearances Is Nothing Then Return
        For i As Integer = 0 To Count - 1
            Try
                Dim wasEnabled As Boolean = Conduits(i).Enabled
                Conduits(i).Enabled = False
                Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
                Conduits(i).Enabled = wasEnabled
            Catch
            End Try
        Next
        UpdateHoverHighlightOverlay()
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub UpdateHoverHighlightOverlay()
        If _hoverAppearanceSlot < 0 OrElse _hoverAppearanceMode = Rhino.UI.Gumball.GumballMode.None Then Return
        Dim slot As Integer = _hoverAppearanceSlot
        Dim mode As Rhino.UI.Gumball.GumballMode = _hoverAppearanceMode
        _hoverAppearanceSlot = -1
        _hoverAppearanceMode = Rhino.UI.Gumball.GumballMode.None
        ApplyHoverAppearance(slot, mode)
    End Sub

    Private Function SnapRadiusForGrip(ix As Integer) As Double
        If Not Double.IsNaN(SnapTranslateRadiusOverride) AndAlso SnapTranslateRadiusOverride > 0 Then
            Return SnapTranslateRadiusOverride
        End If
        Return Double.NaN
    End Function

    Public Sub HideGumballs()
        If Conduits Is Nothing Then Return
        For i As Int32 = 0 To Conduits.Length - 1
            Conduits(i).Enabled = False
        Next
        ClearHoverHighlight()
        Me.Enabled = False
        If Component IsNot Nothing Then
            ViewportPreview.TryRedrawLinkedOrActiveDoc(Component)
        Else
            Try
                Rhino.RhinoDoc.ActiveDoc?.Views.Redraw()
            Catch
            End Try
        End If
    End Sub

    Public Sub Dispose()
        CloseNumericTextBoxIfAny()
        TearDownRhinoEscapeHandler()
        PreviewGripSlot = -1
        PreviewGripDelta = Transform.Identity
        DisposeSnapTranslateTargets()
        ClearHoverHighlight()
        If _hoverHighlightConduit IsNot Nothing Then
            Try
                _hoverHighlightConduit.Dispose()
            Catch
            End Try
            _hoverHighlightConduit = Nothing
        End If
        If _hoverPickConduit IsNot Nothing Then
            Try
                _hoverPickConduit.Dispose()
            Catch
            End Try
            _hoverPickConduit = Nothing
        End If
        If Conduits Is Nothing OrElse Gumballs Is Nothing Then
            Count = 0
            Geometry = Nothing
            Xform = Nothing
            Me.Enabled = False
            Return
        End If
        Dim nSlots As Integer = Math.Min(Math.Min(Math.Max(Count, 0), Conduits.Length), Gumballs.Length)
        For i As Int32 = 0 To nSlots - 1
            Try
                Conduits(i).Enabled = False
                Conduits(i).Dispose()
            Catch
            End Try
            Try
                Gumballs(i).Dispose()
            Catch
            End Try
        Next
        Count = 0
        Geometry = Nothing
        Xform = Nothing
        Gumballs = Nothing
        Conduits = Nothing
        Appearances = Nothing
        Me.Enabled = False
    End Sub
#End Region

#Region "Gumball Transform"

    Public Sub UpdateGumball(ByVal Index As Integer)

        If Not (Conduits(Index).InRelocate) Then
            Dim xform As Transform = Conduits(Index).TotalTransform
            Conduits(Index).PreTransform = xform
        End If
        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(Index).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(Index).Frame

        If (Rhino.ApplicationSettings.ModelAidSettings.GridSnap) Then
            If (Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateFree Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateX Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateY Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZ Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateXY Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateYZ Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZX) Then
                Dim pln As Plane = gbframe.Plane
                pln.Origin = New Point3d(CInt(pln.Origin.X), CInt(pln.Origin.Y), CInt(pln.Origin.Z))
                gbframe.Plane = pln
            End If
        End If
        baseFrame.Plane = gbframe.Plane
        baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
        Gumballs(Index).Frame = baseFrame
        Conduits(Index).SetBaseGumball(Gumballs(Index), Appearances(Index))
        Conduits(Index).Enabled = True

        If Me.Component.ModeValue(2) Then
            ReapplySlotAlignment(Index)
        End If

        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()

    End Sub

    ''' <summary>Move gumball frame to <paramref name="targetPl"/> without changing geometry or PreTransform.</summary>
    Public Sub RelocateSlotToPlane(i As Integer, targetPl As Plane)
        If i < 0 OrElse i >= Count OrElse Not targetPl.IsValid Then Return
        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
        Dim scd As Vector3d = gbframe.ScaleGripDistance
        gbframe.Plane = targetPl
        baseFrame.Plane = targetPl
        baseFrame.ScaleGripDistance = scd
        Gumballs(i).Frame = baseFrame
        Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
        Conduits(i).Enabled = True
    End Sub

    ''' <summary>Move gumball frame origin to <paramref name="pt"/>; keep current axis directions.</summary>
    Public Sub RelocateSlotToPoint(i As Integer, pt As Point3d)
        If i < 0 OrElse i >= Count OrElse Not pt.IsValid Then Return
        Dim pl As Plane = Conduits(i).Gumball.Frame.Plane
        pl.Origin = pt
        RelocateSlotToPlane(i, pl)
    End Sub

    Public Sub UpdateGumball(ByVal i As Integer, xform As Transform)

        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
        Dim pln As Plane = gbframe.Plane
        pln.Transform(xform)

        If (Rhino.ApplicationSettings.ModelAidSettings.GridSnap) Then
            If (Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateFree Or Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateX Or
                    Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateY Or Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZ Or
                    Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateXY Or Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateYZ Or
                    Conduits(i).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZX) Then

                pln.Origin = New Point3d(CInt(pln.Origin.X), CInt(pln.Origin.Y), CInt(pln.Origin.Z))
                gbframe.Plane = pln
            End If
        End If

        baseFrame.Plane = pln
        baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
        Gumballs(i).Frame = baseFrame
        Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
        Conduits(i).Enabled = True

        If Me.Component.ModeValue(2) Then
            ReapplySlotAlignment(i)
        End If


    End Sub

    Public Sub RestoreGumball()
        For i As Int32 = 0 To Count - 1
            Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
            Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
            gbframe.Plane = New Plane(gbframe.Plane.Origin, Vector3d.XAxis, Vector3d.YAxis)
            baseFrame.Plane = gbframe.Plane
            Gumballs(i).Frame = baseFrame
            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
            Conduits(i).Enabled = True
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Public Sub UpdateGumballFromTextBox(ByVal index As Integer, ByVal Transform As Transform)

        Conduits(index).PreTransform = Transform

        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(index).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(index).Frame

        Dim pln As Plane = gbframe.Plane
        pln.Transform(Transform)
        gbframe.Plane = pln
        baseFrame.Plane = gbframe.Plane

        baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance

        Gumballs(index).Frame = baseFrame
        Conduits(index).SetBaseGumball(Gumballs(index), Appearances(index))
        Conduits(index).Enabled = True

        If Me.Component.ModeValue(2) Then
            ReapplySlotAlignment(index)
        End If

        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Private Sub EnsureSlotAlignArray()
        If Count <= 0 Then Return
        If SlotAlignStates Is Nothing OrElse SlotAlignStates.Length <> Count Then
            ReDim SlotAlignStates(Count - 1)
        End If
    End Sub

    Private Sub ClearSlotAlignStateOnly(i As Integer)
        If SlotAlignStates Is Nothing OrElse i < 0 OrElse i >= SlotAlignStates.Length Then Return
        If SlotAlignStates(i).AlignGeo IsNot Nothing Then
            Try
                SlotAlignStates(i).AlignGeo.Dispose()
            Catch
            End Try
        End If
        Dim empty As SlotAlignState
        empty.Kind = SlotAlignKind.None
        empty.AxisPlane = Plane.Unset
        empty.AlignGeo = Nothing
        SlotAlignStates(i) = empty
    End Sub

    Public Sub ClearAllSlotAlign()
        If SlotAlignStates IsNot Nothing Then
            For i As Integer = 0 To SlotAlignStates.Length - 1
                ClearSlotAlignStateOnly(i)
            Next
        End If
        GeometrytoAlign = Nothing
        ClearAlignAxisReference()
    End Sub

    Public Sub ClearSlotAlign(i As Integer)
        If i < 0 OrElse i >= Count Then Return
        EnsureSlotAlignArray()
        ClearSlotAlignStateOnly(i)
    End Sub

    Public Sub SetSlotAlignAxis(slot As Integer, refPl As Plane)
        If slot < 0 OrElse slot >= Count OrElse Not refPl.IsValid Then Return
        EnsureSlotAlignArray()
        ClearSlotAlignStateOnly(slot)
        Dim st As SlotAlignState
        st.Kind = SlotAlignKind.Axis
        st.AxisPlane = refPl
        st.AlignGeo = Nothing
        SlotAlignStates(slot) = st
        AlignSlotToAxisReference(slot)
    End Sub

    Public Sub SetSlotAlignGeometry(slot As Integer, geo As GeometryBase)
        If slot < 0 OrElse slot >= Count OrElse geo Is Nothing Then Return
        EnsureSlotAlignArray()
        ClearSlotAlignStateOnly(slot)
        Dim st As SlotAlignState
        st.Kind = SlotAlignKind.Geometry
        st.AxisPlane = Plane.Unset
        st.AlignGeo = geo
        SlotAlignStates(slot) = st
        AlignSlotToGeometry(slot, geo)
    End Sub

    Private Sub AlignSlotToGeometry(i As Integer, Geo As GeometryBase)
        If i < 0 OrElse i >= Count OrElse Geo Is Nothing Then Return

        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame

        If (Geo.ObjectType = Rhino.DocObjects.ObjectType.Brep) Then
            Dim brp As Brep = DirectCast(Geo, Brep)
            Dim pln As Plane = gbframe.Plane
            Dim cpt As New Point3d
            Dim ci As ComponentIndex
            Dim normal As New Vector3d
            brp.ClosestPoint(gbframe.Plane.Origin, cpt, ci, Nothing, Nothing, 0, normal)
            If Not (ci.ComponentIndexType = ComponentIndexType.BrepFace) Or Not (normal.IsValid) Then
                normal = New Vector3d(pln.Origin - cpt)
            End If
            Dim transform As Transform = Transform.Rotation(pln.ZAxis, normal, pln.Origin)
            pln.Transform(transform)
            gbframe.Plane = pln

        ElseIf (Geo.ObjectType = Rhino.DocObjects.ObjectType.Mesh) Then
            Dim msh As Mesh = DirectCast(Geo, Mesh)
            Dim mshpt As MeshPoint = msh.ClosestMeshPoint(gbframe.Plane.Origin, 0.0)
            Dim pln As Plane = gbframe.Plane
            Dim transform As Transform = Transform.Rotation(pln.ZAxis, msh.NormalAt(mshpt), pln.Origin)
            pln.Transform(transform)
            gbframe.Plane = pln

        ElseIf (Geo.ObjectType = Rhino.DocObjects.ObjectType.Curve) Then
            Dim crv As Curve = DirectCast(Geo, Curve)
            Dim t As New Double
            crv.ClosestPoint(gbframe.Plane.Origin, t)
            Dim pln As New Plane(gbframe.Plane.Origin, crv.TangentAt(t), Vector3d.CrossProduct(New Vector3d(gbframe.Plane.Origin - crv.PointAt(t)), crv.TangentAt(t)))
            gbframe.Plane = pln

        ElseIf (Geo.ObjectType = Rhino.DocObjects.ObjectType.Point) Then
            Dim pt As Rhino.Geometry.Point = DirectCast(Geo, Rhino.Geometry.Point)
            Dim pln As Plane = gbframe.Plane
            Dim transform As Transform = Transform.Rotation(pln.ZAxis, New Vector3d(pln.Origin - pt.Location), pln.Origin)
            pln.Transform(transform)
            gbframe.Plane = pln

        Else
            Return
        End If

        baseFrame.Plane = gbframe.Plane
        baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
        Gumballs(i).Frame = baseFrame
        Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
        Conduits(i).Enabled = True
    End Sub

    Private Sub AlignSlotToAxisReference(i As Integer)
        If i < 0 OrElse i >= Count Then Return
        EnsureSlotAlignArray()
        If SlotAlignStates(i).Kind <> SlotAlignKind.Axis OrElse Not SlotAlignStates(i).AxisPlane.IsValid Then Return
        Dim refPl As Plane = SlotAlignStates(i).AxisPlane
        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
        Dim o As Point3d = gbframe.Plane.Origin
        gbframe.Plane = New Plane(o, refPl.XAxis, refPl.YAxis)
        baseFrame.Plane = gbframe.Plane
        baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
        Gumballs(i).Frame = baseFrame
        Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
        Conduits(i).Enabled = True
    End Sub

    Friend Sub ReapplySlotAlignment(i As Integer)
        If Component Is Nothing OrElse Not Component.ModeValue(2) Then Return
        If i < 0 OrElse i >= Count Then Return
        EnsureSlotAlignArray()
        Select Case SlotAlignStates(i).Kind
            Case SlotAlignKind.Axis
                AlignSlotToAxisReference(i)
            Case SlotAlignKind.Geometry
                If SlotAlignStates(i).AlignGeo IsNot Nothing Then
                    AlignSlotToGeometry(i, SlotAlignStates(i).AlignGeo)
                End If
        End Select
    End Sub

    Public Sub AlignToGeometry(Geo As GeometryBase)

        HasAlignAxisReferencePlane = False
        AlignAxisReferencePlane = Plane.Unset
        GeometrytoAlign = Geo

        For i As Int32 = 0 To Count - 1
            If Geo Is Nothing Then
                ClearSlotAlign(i)
            Else
                SetSlotAlignGeometry(i, Geo.Duplicate())
            End If
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Public Sub ClearAlignAxisReference()
        HasAlignAxisReferencePlane = False
        AlignAxisReferencePlane = Plane.Unset
    End Sub

    Public Sub SetAlignToAxisReference(refPl As Plane)
        If Not refPl.IsValid Then Return
        HasAlignAxisReferencePlane = True
        AlignAxisReferencePlane = refPl
        GeometrytoAlign = Nothing
        For i As Int32 = 0 To Count - 1
            SetSlotAlignAxis(i, refPl)
        Next
    End Sub

    ''' <summary>Align all gumball axes to <see cref="AlignAxisReferencePlane"/>; each gumball keeps its current origin (centre).</summary>
    Public Sub AlignToAxisReferencePlane()
        If Not HasAlignAxisReferencePlane OrElse Not AlignAxisReferencePlane.IsValid Then Return
        For i As Int32 = 0 To Count - 1
            AlignSlotToAxisReference(i)
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Friend Sub ReapplyStoredAlignment()
        If Component Is Nothing OrElse Not Component.ModeValue(2) Then Return
        For i As Integer = 0 To Count - 1
            ReapplySlotAlignment(i)
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    ''' <summary>Commits the current conduit delta to Grasshopper geometry and gumball bases (picked slot <paramref name="gripIndex"/>).</summary>
    Private Sub CommitGripTransform(ByVal gripIndex As Integer, gbxform As Transform)
        If gripIndex < 0 OrElse gripIndex >= Count OrElse Not GumballComp.TransformIsSignificant(gbxform) Then Return

        Select Case Component.EffectiveTransformMode(gripIndex)

            Case 0 'Normal.
                Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                    Xform(gripIndex).CompoundTransforms.Add(t.Duplicate())
                Next
                Xform(gripIndex).ClearCaches()
                Geometry(gripIndex).Transform(gbxform)
                UpdateGumball(gripIndex)
                SyncCurveGumballBaseAfterCommit(gripIndex)

            Case 1 'Apply to all (whole tree from menu, or same input branch from Aa).
                Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                For Each i As Integer In Component.TransformTargetSlots(gripIndex)
                    For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                        Xform(i).CompoundTransforms.Add(t.Duplicate())
                    Next
                    Xform(i).ClearCaches()
                    Geometry(i).Transform(gbxform)
                    If i = gripIndex Then
                        UpdateGumball(i)
                    Else
                        UpdateGumball(i, gbxform)
                    End If
                    SyncCurveGumballBaseAfterCommit(i)
                Next
                Rhino.RhinoDoc.ActiveDoc.Views.Redraw()

            Case 2 'Relocate.
                UpdateGumball(gripIndex)

        End Select
    End Sub
#End Region

#Region "MouseCallback"
    Private Index As Integer = -1
    Private SaveUndo As Boolean = False
    Private TextBox As FormTextBox = Nothing
    Public ValueString As String = String.Empty
    Private _gripDownViewport As Drawing.Point
    Private _gripExceededDragThreshold As Boolean

    ''' <summary>If the numeric float is orphaned (GhGumball MouseDown did not run), drop the WinForms reference only — does not cancel gumball Index.</summary>
    Friend Sub ForgetFloatingTextBox()
        TextBox = Nothing
    End Sub

    ''' <summary>Called when the numeric FormTextBox closes so we do not keep a disposed reference.</summary>
    Friend Sub DetachTextBoxForm()
        ForgetFloatingTextBox()
    End Sub

    ''' <summary>If the user dismisses numeric input with an empty value, clear pick state.</summary>
    Friend Sub CancelPendingNumericInput()
        Dim ix As Integer = Index
        ValueString = String.Empty
        Index = -1
        SaveUndo = False
        _gripExceededDragThreshold = False
        If ix >= 0 AndAlso ix < Count Then
            Try
                Dim c As Rhino.UI.Gumball.GumballDisplayConduit = Conduits(ix)
                c.PickResult.SetToDefault()
                c.SetBaseGumball(Gumballs(ix), Appearances(ix))
                c.Enabled = True
            Catch
            End Try
            Try
                Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
            Catch
            End Try
        End If
        ResumeHoverPoll()
    End Sub
    Private Function TryViewportUpdateGumball(ix As Integer, view As Rhino.Display.RhinoView, viewportPt As Drawing.Point) As Boolean
        If ix < 0 OrElse ix >= Count Then Return False
        If view Is Nothing Then Return False
        Dim c As Rhino.UI.Gumball.GumballDisplayConduit = Conduits(ix)
        If c.PickResult.Mode = Rhino.UI.Gumball.GumballMode.None Then Return False
        c.CheckShiftAndControlKeys()
        Dim wordline As Line = Nothing
        If Not view.MainViewport.GetFrustumLine(CDbl(viewportPt.X), CDbl(viewportPt.Y), wordline) Then
            wordline = Line.Unset
        End If
        Dim cplane As Plane = view.MainViewport.GetConstructionPlane().Plane()
        Dim lp As Double = Nothing
        Rhino.Geometry.Intersect.Intersection.LinePlane(wordline, cplane, lp)
        Dim dragPoint As Point3d = wordline.PointAt(lp)
        If Rhino.ApplicationSettings.ModelAidSettings.GridSnap Then
            Dim m As Rhino.UI.Gumball.GumballMode = c.PickResult.Mode
            If (m = Rhino.UI.Gumball.GumballMode.TranslateFree OrElse m = Rhino.UI.Gumball.GumballMode.TranslateX OrElse
                    m = Rhino.UI.Gumball.GumballMode.TranslateY OrElse m = Rhino.UI.Gumball.GumballMode.TranslateZ OrElse
                    m = Rhino.UI.Gumball.GumballMode.TranslateXY OrElse m = Rhino.UI.Gumball.GumballMode.TranslateYZ OrElse
                    m = Rhino.UI.Gumball.GumballMode.TranslateZX) Then
                Dim snap As Point3d = New Point3d(CInt(dragPoint.X), CInt(dragPoint.Y), CInt(dragPoint.Z))
                wordline.Transform(Transform.Translation(New Vector3d(snap - dragPoint)))
                dragPoint = snap
            End If
        End If

        If Not c.UpdateGumball(dragPoint, wordline) Then Return False

        ' Geometry snapping: after the raw (constrained) update, stick the gumball origin to the target under the cursor.
        If SnapTranslateActive(c.PickResult.Mode, ix) Then
            Dim snapDelta As Vector3d
            If TryComputeSnapTranslateDelta(c, view, viewportPt, ix, snapDelta) Then
                dragPoint += snapDelta
                If wordline.IsValid Then wordline.Transform(Transform.Translation(snapDelta))
                c.UpdateGumball(dragPoint, wordline)
            End If
        End If
        Return True
    End Function

    ''' <summary>Screen-space snap radius (pixels) used when no t input override is supplied.</summary>
    Private Const SnapTranslateScreenRadiusPx As Double = 15.0R

    Private Function SnapTranslateActive(mode As Rhino.UI.Gumball.GumballMode, gripIx As Integer) As Boolean
        If Not IsTranslateGumballMode(mode) Then Return False
        If Component Is Nothing OrElse Not CBool(Component.ModeValue(3)) Then Return False
        Dim targets As List(Of GeometryBase) = SnapTargetsForSlot(gripIx)
        Return targets IsNot Nothing AndAlso targets.Count > 0
    End Function

    ''' <summary>One snap candidate: a target point plus its osnap class (0 = vertex, 1 = curve/edge, 2 = surface/face).</summary>
    Private Structure SnapCandidate
        Public Q As Point3d
        Public Rank As Integer
    End Structure

    ''' <summary>Max vertices to enumerate as rank-0 candidates on meshes (guards against huge meshes).</summary>
    Private Const SnapMeshVertexLimit As Integer = 20000

    ''' <summary>
    ''' Collects snap candidates for one target: vertices (brep corners, curve endpoints, mesh vertices,
    ''' points), edge/curve points (global closest-pair to the pick ray), and face/surface points
    ''' (all ray hits, plus a sampled silhouette fallback when the ray misses).
    ''' </summary>
    Private Shared Sub CollectSnapCandidates(geom As GeometryBase, ray As Line, cands As List(Of SnapCandidate))
        If geom Is Nothing Then Return
        Try
            If Not geom.IsValid Then Return
        Catch
            Return
        End Try

        Try
            Dim pt As Rhino.Geometry.Point = TryCast(geom, Rhino.Geometry.Point)
            If pt IsNot Nothing Then
                If pt.Location.IsValid Then cands.Add(New SnapCandidate With {.Q = pt.Location, .Rank = 0})
                Return
            End If

            Dim cloud As PointCloud = TryCast(geom, PointCloud)
            If cloud IsNot Nothing Then
                Dim best As Point3d = Point3d.Unset
                Dim bestD2 As Double = Double.PositiveInfinity
                For Each item As PointCloudItem In cloud
                    Dim d2 As Double = item.Location.DistanceToSquared(ray.ClosestPoint(item.Location, False))
                    If d2 < bestD2 Then
                        bestD2 = d2
                        best = item.Location
                    End If
                Next
                If best.IsValid Then cands.Add(New SnapCandidate With {.Q = best, .Rank = 0})
                Return
            End If

            Dim crv As Curve = TryCast(geom, Curve)
            If crv IsNot Nothing Then
                CollectCurveCandidates(crv, ray, cands, 0, 1)
                Return
            End If

            Dim brep As Brep = TryCast(geom, Brep)
            Dim ownedBrep As Brep = Nothing
            If brep Is Nothing Then
                Dim ext As Extrusion = TryCast(geom, Extrusion)
                If ext IsNot Nothing Then
                    ownedBrep = ext.ToBrep()
                Else
                    Dim srf As Surface = TryCast(geom, Surface)
                    If srf IsNot Nothing Then ownedBrep = Brep.CreateFromSurface(srf)
                End If
                brep = ownedBrep
            End If
            If brep IsNot Nothing Then
                Try
                    CollectBrepCandidates(brep, ray, cands)
                Finally
                    If ownedBrep IsNot Nothing Then ownedBrep.Dispose()
                End Try
                Return
            End If

            Dim mesh As Mesh = TryCast(geom, Mesh)
            Dim ownedMesh As Mesh = Nothing
            If mesh Is Nothing Then
                Dim subd As SubD = TryCast(geom, SubD)
                If subd IsNot Nothing Then
                    ownedMesh = Mesh.CreateFromSubD(subd, 2)
                    mesh = ownedMesh
                End If
            End If
            If mesh IsNot Nothing Then
                Try
                    CollectMeshCandidates(mesh, ray, cands)
                Finally
                    If ownedMesh IsNot Nothing Then ownedMesh.Dispose()
                End Try
                Return
            End If

            ' Unknown type: sampled surface-rank candidate.
            Dim q As Point3d
            If TrySampledClosestToRay(geom, ray, q) Then
                cands.Add(New SnapCandidate With {.Q = q, .Rank = 2})
            End If
        Catch
        End Try
    End Sub

    ''' <summary>Endpoints as vertex-rank candidates; the globally nearest on-curve point as curve-rank.</summary>
    Private Shared Sub CollectCurveCandidates(crv As Curve, ray As Line, cands As List(Of SnapCandidate), vertexRank As Integer, curveRank As Integer)
        If Not crv.IsClosed Then
            If crv.PointAtStart.IsValid Then cands.Add(New SnapCandidate With {.Q = crv.PointAtStart, .Rank = vertexRank})
            If crv.PointAtEnd.IsValid Then cands.Add(New SnapCandidate With {.Q = crv.PointAtEnd, .Rank = vertexRank})
        End If
        Dim ptCrv As Point3d = Nothing
        Dim ptRay As Point3d = Nothing
        Using lc As New LineCurve(ray)
            If crv.ClosestPoints(lc, ptCrv, ptRay) AndAlso ptCrv.IsValid Then
                cands.Add(New SnapCandidate With {.Q = ptCrv, .Rank = curveRank})
                Return
            End If
        End Using
        Dim q As Point3d
        If TrySampledClosestToRay(crv, ray, q) Then
            cands.Add(New SnapCandidate With {.Q = q, .Rank = curveRank})
        End If
    End Sub

    ''' <summary>Brep corners (rank 0), edges (rank 1), and face ray hits / sampled silhouette (rank 2).</summary>
    Private Shared Sub CollectBrepCandidates(brep As Brep, ray As Line, cands As List(Of SnapCandidate))
        For Each v As BrepVertex In brep.Vertices
            If v.Location.IsValid Then cands.Add(New SnapCandidate With {.Q = v.Location, .Rank = 0})
        Next

        For Each edge As BrepEdge In brep.Edges
            Dim ptCrv As Point3d = Nothing
            Dim ptRay As Point3d = Nothing
            Using lc As New LineCurve(ray)
                If edge.ClosestPoints(lc, ptCrv, ptRay) AndAlso ptCrv.IsValid Then
                    cands.Add(New SnapCandidate With {.Q = ptCrv, .Rank = 1})
                End If
            End Using
        Next

        Dim tol As Double = 0.001
        Try
            tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
        Catch
        End Try
        Dim overlaps As Curve() = Nothing
        Dim hits As Point3d() = Nothing
        Dim anyHit As Boolean = False
        Using lc As New LineCurve(ray)
            If Rhino.Geometry.Intersect.Intersection.CurveBrep(lc, brep, tol, overlaps, hits) AndAlso hits IsNot Nothing Then
                For Each h As Point3d In hits
                    If h.IsValid Then
                        cands.Add(New SnapCandidate With {.Q = h, .Rank = 2})
                        anyHit = True
                    End If
                Next
            End If
        End Using
        If Not anyHit Then
            Dim q As Point3d
            If TrySampledClosestToRay(brep, ray, q) Then
                cands.Add(New SnapCandidate With {.Q = q, .Rank = 2})
            End If
        End If
    End Sub

    ''' <summary>Mesh: nearest vertex to the ray (rank 0, size-guarded) and face ray hits / sampled fallback (rank 2).</summary>
    Private Shared Sub CollectMeshCandidates(mesh As Mesh, ray As Line, cands As List(Of SnapCandidate))
        If mesh.Vertices.Count <= SnapMeshVertexLimit Then
            Dim best As Point3d = Point3d.Unset
            Dim bestD2 As Double = Double.PositiveInfinity
            For i As Integer = 0 To mesh.Vertices.Count - 1
                Dim vp As Point3d = mesh.Vertices(i)
                Dim d2 As Double = vp.DistanceToSquared(ray.ClosestPoint(vp, False))
                If d2 < bestD2 Then
                    bestD2 = d2
                    best = vp
                End If
            Next
            If best.IsValid Then cands.Add(New SnapCandidate With {.Q = best, .Rank = 0})
        End If

        Dim hits As Point3d() = Rhino.Geometry.Intersect.Intersection.MeshLine(mesh, ray)
        Dim anyHit As Boolean = False
        If hits IsNot Nothing Then
            For Each h As Point3d In hits
                If h.IsValid Then
                    cands.Add(New SnapCandidate With {.Q = h, .Rank = 2})
                    anyHit = True
                End If
            Next
        End If
        If Not anyHit Then
            Dim q As Point3d
            If TrySampledClosestToRay(mesh, ray, q) Then
                cands.Add(New SnapCandidate With {.Q = q, .Rank = 2})
            End If
        End If
    End Sub

    ''' <summary>
    ''' Global search fallback: sample the pick ray densely, project each sample onto the geometry,
    ''' keep the candidate nearest the ray, then polish with a few alternating projections.
    ''' </summary>
    Private Shared Function TrySampledClosestToRay(geom As GeometryBase, ray As Line, ByRef q As Point3d) As Boolean
        Const samples As Integer = 48
        Dim best As Point3d = Point3d.Unset
        Dim bestD2 As Double = Double.PositiveInfinity
        For i As Integer = 0 To samples
            Dim p As Point3d = ray.PointAt(CDbl(i) / CDbl(samples))
            Dim cq As Point3d
            If Not TryClosestPointOnSnapGeometry(geom, p, cq) Then Continue For
            Dim d2 As Double = cq.DistanceToSquared(ray.ClosestPoint(cq, False))
            If d2 < bestD2 Then
                bestD2 = d2
                best = cq
            End If
        Next
        If Not best.IsValid Then Return False

        For it As Integer = 0 To 2
            Dim rp As Point3d = ray.ClosestPoint(best, False)
            Dim refined As Point3d
            If Not TryClosestPointOnSnapGeometry(geom, rp, refined) Then Exit For
            best = refined
        Next
        q = best
        Return q.IsValid
    End Function

    ''' <summary>
    ''' Ray-based snapping (osnap feel): candidates are the target points nearest the mouse pick ray,
    ''' accepted when they lie within the pixel radius of the cursor (or the model-space t override,
    ''' measured ray-to-target). The winning point is applied through the active translate constraint.
    ''' </summary>
    Private Function TryComputeSnapTranslateDelta(c As Rhino.UI.Gumball.GumballDisplayConduit, view As Rhino.Display.RhinoView, viewportPt As Drawing.Point, gripIx As Integer, ByRef delta As Vector3d) As Boolean
        delta = Vector3d.Zero
        If view Is Nothing Then Return False

        ' c.Gumball is the *current* (dragged) gumball: BaseGumball with GumballTransform applied.
        Dim dragPlane As Plane
        Try
            dragPlane = c.Gumball.Frame.Plane
        Catch
            Return False
        End Try
        Dim draggedOrigin As Point3d = dragPlane.Origin
        If Not draggedOrigin.IsValid Then Return False

        Dim ray As Line = Nothing
        If Not view.MainViewport.GetFrustumLine(CDbl(viewportPt.X), CDbl(viewportPt.Y), ray) Then Return False
        If Not ray.IsValid Then Return False

        Dim overrideRadius As Double = SnapRadiusForGrip(gripIx)

        Dim cursor As New Rhino.Geometry.Point2d(CDbl(viewportPt.X), CDbl(viewportPt.Y))
        Dim cameraLocation As Point3d = ray.To
        Try
            Dim camPt As Point3d = view.MainViewport.CameraLocation
            If camPt.IsValid Then cameraLocation = camPt
        Catch
        End Try

        ' Candidates from this gumball slot's snap targets only.
        Dim slotTargets As List(Of GeometryBase) = SnapTargetsForSlot(gripIx)
        If slotTargets Is Nothing OrElse slotTargets.Count = 0 Then Return False

        Dim cands As New List(Of SnapCandidate)
        For Each geom As GeometryBase In slotTargets
            CollectSnapCandidates(geom, ray, cands)
        Next
        If cands.Count = 0 Then Return False

        ' Selection: only in-radius candidates compete. Vertices beat edges beat faces;
        ' within vertices/edges the one closest to the cursor wins; within faces the
        ' front-most hit (closest to the camera along the ray) wins.
        Dim bestQ As Point3d = Point3d.Unset
        Dim bestRank As Integer = Integer.MaxValue
        Dim bestMetric As Double = Double.PositiveInfinity

        For Each cand As SnapCandidate In cands
            Dim q As Point3d = cand.Q
            If Not q.IsValid Then Continue For

            If Not Double.IsNaN(overrideRadius) Then
                ' t input: fixed model-space radius, measured from the pick ray to the candidate.
                If q.DistanceTo(ray.ClosestPoint(q, False)) > overrideRadius Then Continue For
            Else
                ' Default: what's visually near the cursor snaps, regardless of depth.
                Dim sq As Rhino.Geometry.Point2d = view.MainViewport.WorldToClient(q)
                Dim dPx As Double = Math.Sqrt((sq.X - cursor.X) * (sq.X - cursor.X) + (sq.Y - cursor.Y) * (sq.Y - cursor.Y))
                If dPx > SnapTranslateScreenRadiusPx Then Continue For
            End If

            Dim metric As Double
            If cand.Rank >= 2 Then
                ' Faces/surfaces: depth — nearest to the camera wins.
                ' (GetFrustumLine runs far→near, so ray.From is the FAR plane; use the camera itself.)
                metric = q.DistanceToSquared(cameraLocation)
            Else
                ' Vertices and edges: cursor proximity in pixels.
                Dim sq2 As Rhino.Geometry.Point2d = view.MainViewport.WorldToClient(q)
                metric = (sq2.X - cursor.X) * (sq2.X - cursor.X) + (sq2.Y - cursor.Y) * (sq2.Y - cursor.Y)
            End If

            If cand.Rank < bestRank OrElse (cand.Rank = bestRank AndAlso metric < bestMetric) Then
                bestRank = cand.Rank
                bestMetric = metric
                bestQ = q
            End If
        Next
        If Not bestQ.IsValid Then Return False

        ' Keep only the components of the correction the active grip is allowed to move along.
        delta = ConstrainSnapVectorToMode(c.PickResult.Mode, dragPlane, bestQ - draggedOrigin)
        Return delta.Length > Rhino.RhinoMath.ZeroTolerance
    End Function

    ''' <summary>Projects the snap correction vector onto the translation constraint (axis, plane, or free).</summary>
    Private Shared Function ConstrainSnapVectorToMode(mode As Rhino.UI.Gumball.GumballMode, dragPlane As Plane, v As Vector3d) As Vector3d
        Select Case mode
            Case Rhino.UI.Gumball.GumballMode.TranslateX
                Return dragPlane.XAxis * (v * dragPlane.XAxis)
            Case Rhino.UI.Gumball.GumballMode.TranslateY
                Return dragPlane.YAxis * (v * dragPlane.YAxis)
            Case Rhino.UI.Gumball.GumballMode.TranslateZ
                Return dragPlane.ZAxis * (v * dragPlane.ZAxis)
            Case Rhino.UI.Gumball.GumballMode.TranslateXY
                Return v - dragPlane.ZAxis * (v * dragPlane.ZAxis)
            Case Rhino.UI.Gumball.GumballMode.TranslateYZ
                Return v - dragPlane.XAxis * (v * dragPlane.XAxis)
            Case Rhino.UI.Gumball.GumballMode.TranslateZX
                Return v - dragPlane.YAxis * (v * dragPlane.YAxis)
            Case Else ' TranslateFree
                Return v
        End Select
    End Function

    ''' <summary>Closest point on any supported snap target geometry. Type dispatch by TryCast so curve/surface subclasses always resolve.</summary>
    Private Shared Function TryClosestPointOnSnapGeometry(geom As GeometryBase, p As Point3d, ByRef q As Point3d) As Boolean
        If geom Is Nothing Then Return False
        Try
            If Not geom.IsValid Then Return False
        Catch
            Return False
        End Try

        Try
            Dim pt As Rhino.Geometry.Point = TryCast(geom, Rhino.Geometry.Point)
            If pt IsNot Nothing Then
                q = pt.Location
                Return q.IsValid
            End If

            Dim cloud As PointCloud = TryCast(geom, PointCloud)
            If cloud IsNot Nothing Then
                If cloud.Count = 0 Then Return False
                Dim ix As Integer = cloud.ClosestPoint(p)
                If ix < 0 Then Return False
                q = cloud(ix).Location
                Return q.IsValid
            End If

            Dim crv As Curve = TryCast(geom, Curve)
            If crv IsNot Nothing Then
                Dim t As Double
                If Not crv.ClosestPoint(p, t) Then Return False
                q = crv.PointAt(t)
                Return q.IsValid
            End If

            Dim brep As Brep = TryCast(geom, Brep)
            If brep IsNot Nothing Then
                Return TryClosestBrepSnapPoint(brep, p, q)
            End If

            ' Extrusion before generic Surface: capped solids need the full brep (walls + caps).
            Dim ext As Extrusion = TryCast(geom, Extrusion)
            If ext IsNot Nothing Then
                Dim tb As Brep = ext.ToBrep()
                If tb Is Nothing Then Return False
                Try
                    Return TryClosestBrepSnapPoint(tb, p, q)
                Finally
                    tb.Dispose()
                End Try
            End If

            Dim srf As Surface = TryCast(geom, Surface)
            If srf IsNot Nothing Then
                Dim u, v As Double
                If Not srf.ClosestPoint(p, u, v) Then Return False
                q = srf.PointAt(u, v)
                Return q.IsValid
            End If

            Dim mesh As Mesh = TryCast(geom, Mesh)
            If mesh IsNot Nothing Then
                Dim mp As MeshPoint = mesh.ClosestMeshPoint(p, 0.0#)
                If mp Is Nothing Then Return False
                q = mesh.PointAt(mp)
                Return q.IsValid
            End If

            Dim subd As SubD = TryCast(geom, SubD)
            If subd IsNot Nothing Then
                Dim sm As Mesh = Mesh.CreateFromSubD(subd, 2)
                If sm Is Nothing Then Return False
                Try
                    Dim smp As MeshPoint = sm.ClosestMeshPoint(p, 0.0#)
                    If smp Is Nothing Then Return False
                    q = sm.PointAt(smp)
                    Return q.IsValid
                Finally
                    sm.Dispose()
                End Try
            End If
        Catch
            Return False
        End Try

        Return False
    End Function

    Private Shared Function TryClosestBrepSnapPoint(brep As Brep, p As Point3d, ByRef q As Point3d) As Boolean
        Dim cpt As New Point3d
        Dim ci As ComponentIndex
        Dim nv As Vector3d
        If brep.ClosestPoint(p, cpt, ci, Nothing, Nothing, 0, nv) AndAlso cpt.IsValid Then
            q = cpt
            Return True
        End If
        ' Some invalid-ish breps fail the component search; fall back to per-face closest point.
        Dim bestD2 As Double = Double.PositiveInfinity
        Dim found As Boolean = False
        For Each f As BrepFace In brep.Faces
            Dim u, v As Double
            If Not f.ClosestPoint(p, u, v) Then Continue For
            Dim fp As Point3d = f.PointAt(u, v)
            If Not fp.IsValid Then Continue For
            Dim d2 As Double = p.DistanceToSquared(fp)
            If d2 < bestD2 Then
                bestD2 = d2
                q = fp
                found = True
            End If
        Next
        Return found
    End Function

    ''' <summary>Axis / rotate / scale grips support typed values; planar and free translate are drag-only.</summary>
    Private Shared Function SupportsNumericGripEntry(mode As Rhino.UI.Gumball.GumballMode) As Boolean
        Select Case mode
            Case Rhino.UI.Gumball.GumballMode.TranslateX,
                    Rhino.UI.Gumball.GumballMode.TranslateY,
                    Rhino.UI.Gumball.GumballMode.TranslateZ,
                    Rhino.UI.Gumball.GumballMode.RotateX,
                    Rhino.UI.Gumball.GumballMode.RotateY,
                    Rhino.UI.Gumball.GumballMode.RotateZ,
                    Rhino.UI.Gumball.GumballMode.ScaleX,
                    Rhino.UI.Gumball.GumballMode.ScaleY,
                    Rhino.UI.Gumball.GumballMode.ScaleZ
                Return True
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>Priming translate grips via UpdateGumball at a CPlane pick introduces a bogus translation versus axis-only numeric moves; numeric entry must leave the conduit on the stored base gumball.</summary>
    Private Shared Function IsTranslateGumballMode(mode As Rhino.UI.Gumball.GumballMode) As Boolean
        Select Case mode
            Case Rhino.UI.Gumball.GumballMode.TranslateX,
                    Rhino.UI.Gumball.GumballMode.TranslateY,
                    Rhino.UI.Gumball.GumballMode.TranslateZ,
                    Rhino.UI.Gumball.GumballMode.TranslateFree,
                    Rhino.UI.Gumball.GumballMode.TranslateXY,
                    Rhino.UI.Gumball.GumballMode.TranslateYZ,
                    Rhino.UI.Gumball.GumballMode.TranslateZX
                Return True
            Case Else
                Return False
        End Select
    End Function

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        CloseNumericTextBoxIfAny()
        Index = -1

        If (e.Button <> MouseButtons.Left) Then Exit Sub
        If Component IsNot Nothing AndAlso Not ViewportPreview.IsLinkedRhinoDocumentActive(Component) Then Exit Sub
        If e.View IsNot Nothing AndAlso e.View.Document IsNot Nothing AndAlso Component IsNot Nothing Then
            Dim owner As Rhino.RhinoDoc = Component.PreviewRhinoDoc
            If owner Is Nothing Then
                Dim ghDoc As GH_Document = Component.OnPingDocument()
                owner = If(ghDoc IsNot Nothing, ghDoc.RhinoDocument, Nothing)
            End If
            If owner IsNot Nothing AndAlso Not ReferenceEquals(e.View.Document, owner) Then Exit Sub
        End If

        Dim Pick As New Rhino.Input.Custom.PickContext
        Pick.View = e.View
        Pick.PickStyle = Rhino.Input.Custom.PickStyle.PointPick
        Pick.SetPickTransform(e.View.ActiveViewport.GetPickTransform(e.ViewportPoint))
        Dim pickline As Line = Nothing
        e.View.ActiveViewport.GetFrustumLine(CDbl(e.ViewportPoint.X), CDbl(e.ViewportPoint.Y), pickline)
        Pick.PickLine = pickline
        Pick.UpdateClippingPlanes()

        For i As Int32 = 0 To Count - 1
            If Component IsNot Nothing AndAlso Component.SlotSettings IsNot Nothing AndAlso i < Component.SlotSettings.Length Then
                If Not Component.SlotSettings(i).Active Then Continue For
            End If
            If (Conduits(i).PickGumball(Pick, Nothing)) Then
                Index = i
                SaveUndo = True
                e.Cancel = True
                _gripDownViewport = e.ViewportPoint
                _gripExceededDragThreshold = False
                PreviewGripDelta = Transform.Identity
                PreviewGripSlot = -1
                _dragPreTransformSnapshot = Conduits(i).PreTransform
                _dragPreTransformCaptured = True
                EnsureRhinoEscapeHandler()
                If _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = False
                Exit For
            End If
        Next

    End Sub

    Private Sub EnsureRhinoEscapeHandler()
        If _rhinoEscapeSubscribed Then Return
        AddHandler Rhino.RhinoApp.EscapeKeyPressed, AddressOf OnRhinoEscapePressed
        _rhinoEscapeSubscribed = True
    End Sub

    Private Sub TearDownRhinoEscapeHandler()
        If Not _rhinoEscapeSubscribed Then Return
        RemoveHandler Rhino.RhinoApp.EscapeKeyPressed, AddressOf OnRhinoEscapePressed
        _rhinoEscapeSubscribed = False
    End Sub

    Private Sub OnRhinoEscapePressed(sender As Object, e As EventArgs)
        If TextBox IsNot Nothing Then Return
        If Index < 0 OrElse Index >= Count Then Return
        CancelActiveGripDragViaEscape()
    End Sub

    ''' <summary>Abort in-viewport drag (move/rotate/scale) without committing GumballTransform to geometry.</summary>
    Private Sub CancelActiveGripDragViaEscape()

        Dim ix As Integer = Index

        PreviewGripSlot = -1
        PreviewGripDelta = Transform.Identity

        If ix >= 0 AndAlso ix < Count Then
            Try
                Dim c As Rhino.UI.Gumball.GumballDisplayConduit = Conduits(ix)
                If _dragPreTransformCaptured Then
                    c.PreTransform = _dragPreTransformSnapshot
                End If
                c.PickResult.SetToDefault()
                c.SetBaseGumball(Gumballs(ix), Appearances(ix))
                c.Enabled = True
                SaveUndo = False
            Catch
            End Try
        End If
        _dragPreTransformCaptured = False
        _gripExceededDragThreshold = False
        Index = -1
        Me.Enabled = True

        If Component IsNot Nothing Then
            Try
                Component.ExpireSolution(True)
            Catch
            End Try
        End If

        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        TearDownRhinoEscapeHandler()
        ResumeHoverPoll()
    End Sub

    ''' <summary>Closes the numeric float and clears pending pick state; use Close() only (no Dispose-before-Close) so the native window is destroyed on macOS.</summary>
    Private Sub CloseNumericTextBoxIfAny()
        If TextBox Is Nothing Then Return
        Dim tb As FormTextBox = TextBox
        TextBox = Nothing
        tb.DismissWithoutCommit()
    End Sub

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)

        If (TextBox IsNot Nothing) Then
            e.Cancel = True
            Exit Sub
        End If

        If Index < 0 Then
            PollHoverFromGlobalCursor()
            Exit Sub
        End If

        If (Index >= Count) Then Exit Sub

        If (Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.None) Then Exit Sub

        Dim ddx As Double = CDbl(e.ViewportPoint.X) - CDbl(_gripDownViewport.X)
        Dim ddy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_gripDownViewport.Y)
        If Not _gripExceededDragThreshold Then
            Const clickSlopPx As Double = 4.0
            If (ddx * ddx + ddy * ddy) < (clickSlopPx * clickSlopPx) Then
                e.Cancel = True
                Exit Sub
            End If
            _gripExceededDragThreshold = True
            If Component IsNot Nothing Then Component.SetHoverTarget(-1, Rhino.UI.Gumball.GumballMode.None)
        End If

        If Not TryViewportUpdateGumball(Index, e.View, e.ViewportPoint) Then Exit Sub
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()

        If Component IsNot Nothing AndAlso Component.LiveTransformsWhileDragging Then
            ' Always expose the conduit delta while the mouse is still down once past pixel slop.
            ' Clearing preview when GumballTransform is ~identity clears ApplyAlignGeometryInput deferral and resets the conduit mid-drag ("sticky return to origin").
            PreviewGripSlot = Index
            PreviewGripDelta = Conduits(Index).GumballTransform
            Try
                Component.ExpireSolution(True)
            Catch
            End Try
        End If

        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        TearDownRhinoEscapeHandler()

        MyBase.OnMouseUp(e)

        ' Numeric float keeps Index until Enter/dismiss; avoid clearing pick state here.
        If (TextBox IsNot Nothing) Then
            e.Cancel = True
            Exit Sub
        End If

        If (Index = -1) Or (Index >= Count) Or (ValueString <> String.Empty) Then Exit Sub

        ' Click without drag: numeric entry for axis / rotate / scale only (planar translate is drag-only).
        If Not _gripExceededDragThreshold Then
            _dragPreTransformCaptured = False
            SaveUndo = False
            PreviewGripSlot = -1
            PreviewGripDelta = Transform.Identity
            Dim ixPrimed As Integer = Index
            If ixPrimed >= 0 AndAlso ixPrimed < Count AndAlso
                Not SupportsNumericGripEntry(Conduits(ixPrimed).PickResult.Mode) Then
                Try
                    Dim c As Rhino.UI.Gumball.GumballDisplayConduit = Conduits(ixPrimed)
                    c.PickResult.SetToDefault()
                    c.SetBaseGumball(Gumballs(ixPrimed), Appearances(ixPrimed))
                Catch
                End Try
                Index = -1
                Try
                    Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
                Catch
                End Try
                ResumeHoverPoll()
                e.Cancel = True
                Exit Sub
            End If
            Try
                If e.View IsNot Nothing Then
                    If ixPrimed >= 0 AndAlso ixPrimed < Count AndAlso Not IsTranslateGumballMode(Conduits(ixPrimed).PickResult.Mode) Then
                        If TryViewportUpdateGumball(ixPrimed, e.View, _gripDownViewport) Then
                            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
                        End If
                    Else
                        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
                    End If
                End If
            Catch
            End Try
            Dim screenPt As Drawing.Point = Control.MousePosition
            TextBox = New FormTextBox(screenPt, Me)
            e.Cancel = True
            Exit Sub
        End If

        _dragPreTransformCaptured = False

        Dim gripIdx As Integer = Index
        Try
            If e.View IsNot Nothing AndAlso gripIdx >= 0 AndAlso gripIdx < Count Then
                TryViewportUpdateGumball(gripIdx, e.View, e.ViewportPoint)
            End If
        Catch
        End Try
        Dim finalXform As Transform = Conduits(gripIdx).GumballTransform

        PreviewGripSlot = -1
        PreviewGripDelta = Transform.Identity

        If SaveUndo AndAlso GumballComp.TransformIsSignificant(finalXform) Then
            Component.RecordUndoEvent("Gumball Drag", New GbUndo(Me))
        End If
        SaveUndo = False

        If GumballComp.TransformIsSignificant(finalXform) Then
            CommitGripTransform(gripIdx, finalXform)
        Else
            Try
                Dim c As Rhino.UI.Gumball.GumballDisplayConduit = Conduits(gripIdx)
                c.PickResult.SetToDefault()
                c.SetBaseGumball(Gumballs(gripIdx), Appearances(gripIdx))
                c.Enabled = True
            Catch
            End Try
        End If

        If Component.LiveTransformsWhileDragging OrElse GumballComp.TransformIsSignificant(finalXform) Then
            Component.ExpireSolution(True)
        End If

        _gripExceededDragThreshold = False
        Index = -1
        ResumeHoverPoll()
        e.Cancel = True
    End Sub

    Public Sub TransformFromTextBox()

        If String.IsNullOrWhiteSpace(ValueString) OrElse (Index < 0) OrElse (Index >= Count) Then
            Exit Sub
        End If

            Me.Component.RecordUndoEvent("Gumball Drag", New GbUndo(Me))

            Dim gbxform As Transform = Conduits(Index).GumballTransform

            Dim value As New Double
            Try
                value = Convert.ToDouble(ValueString)

            Catch ex As Exception
                Rhino.RhinoApp.WriteLine("Invalid value. Only numerical values are allowed.")
                Index = -1
                ValueString = String.Empty
                _gripExceededDragThreshold = False
                Exit Sub
            End Try

            Dim pln As Plane = Conduits(Index).Gumball.Frame.Plane

            Select Case Conduits(Index).PickResult.Mode
                Case Rhino.UI.Gumball.GumballMode.TranslateX
                    gbxform = Transform.Translation(pln.XAxis * value)

                Case Rhino.UI.Gumball.GumballMode.TranslateY
                    gbxform = Transform.Translation(pln.YAxis * value)

                Case Rhino.UI.Gumball.GumballMode.TranslateZ
                    gbxform = Transform.Translation(pln.ZAxis * value)

                Case Rhino.UI.Gumball.GumballMode.RotateX
                    gbxform = Transform.Rotation((value * Math.PI) / 180, pln.XAxis, pln.Origin)

                Case Rhino.UI.Gumball.GumballMode.RotateY
                    gbxform = Transform.Rotation((value * Math.PI) / 180, pln.YAxis, pln.Origin)

                Case Rhino.UI.Gumball.GumballMode.RotateZ
                    gbxform = Transform.Rotation((value * Math.PI) / 180, pln.ZAxis, pln.Origin)

                Case Rhino.UI.Gumball.GumballMode.ScaleX
                    gbxform = Transform.Scale(pln, value, 1, 1)
                    If Component.SlotAppliesToAll(Index) Then 'Apply to all.
                        For Each i As Integer In Component.TransformTargetSlots(Index)
                            Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
                            frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X * value, frame.ScaleGripDistance.Y, frame.ScaleGripDistance.Z)
                            Gumballs(i).Frame = frame
                            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
                        Next
                    Else
                        Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(Index).Gumball.Frame
                        frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X * value, frame.ScaleGripDistance.Y, frame.ScaleGripDistance.Z)
                        Gumballs(Index).Frame = frame
                        Conduits(Index).SetBaseGumball(Gumballs(Index), Appearances(Index))
                    End If

                Case Rhino.UI.Gumball.GumballMode.ScaleY
                    gbxform = Transform.Scale(pln, 1, value, 1)
                    If Component.SlotAppliesToAll(Index) Then 'Apply to all.
                        For Each i As Integer In Component.TransformTargetSlots(Index)
                            Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
                            frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X, frame.ScaleGripDistance.Y * value, frame.ScaleGripDistance.Z)
                            Gumballs(i).Frame = frame
                            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
                        Next
                    Else
                        Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(Index).Gumball.Frame
                        frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X, frame.ScaleGripDistance.Y * value, frame.ScaleGripDistance.Z)
                        Gumballs(Index).Frame = frame
                        Conduits(Index).SetBaseGumball(Gumballs(Index), Appearances(Index))
                    End If

                Case Rhino.UI.Gumball.GumballMode.ScaleZ
                    gbxform = Transform.Scale(pln, 1, 1, value)
                    If Component.SlotAppliesToAll(Index) Then 'Apply to all.
                        For Each i As Integer In Component.TransformTargetSlots(Index)
                            Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
                            frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X, frame.ScaleGripDistance.Y, frame.ScaleGripDistance.Z * value)
                            Gumballs(i).Frame = frame
                            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
                        Next
                    Else
                        Dim frame As Rhino.UI.Gumball.GumballFrame = Conduits(Index).Gumball.Frame
                        frame.ScaleGripDistance = New Vector3d(frame.ScaleGripDistance.X, frame.ScaleGripDistance.Y, frame.ScaleGripDistance.Z * value)
                        Gumballs(Index).Frame = frame
                        Conduits(Index).SetBaseGumball(Gumballs(Index), Appearances(Index))
                    End If

                Case Else
                    Index = -1
                    ValueString = String.Empty
                    _gripExceededDragThreshold = False
                    Exit Sub

            End Select

            If GumballComp.TransformIsSignificant(gbxform) Then

                Select Case Component.EffectiveTransformMode(Index)

                    Case 0 'Normal
                        Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                            Xform(Index).CompoundTransforms.Add(t.Duplicate())
                        Next
                        Xform(Index).ClearCaches()
                        Geometry(Index).Transform(gbxform)
                        UpdateGumballFromTextBox(Index, gbxform)
                        SyncCurveGumballBaseAfterCommit(Index)

                    Case 1 'Apply to all.
                        Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                        For Each i As Integer In Component.TransformTargetSlots(Index)
                            For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                                Xform(i).CompoundTransforms.Add(t.Duplicate())
                            Next
                            Xform(i).ClearCaches()
                            Geometry(i).Transform(gbxform)
                            UpdateGumballFromTextBox(i, gbxform)
                            SyncCurveGumballBaseAfterCommit(i)
                        Next

                    Case 2 'Relocate.
                        UpdateGumballFromTextBox(Index, gbxform)

                End Select
                Component.ExpireSolution(True)
            End If
            Index = -1
            ValueString = String.Empty
            _gripExceededDragThreshold = False
    End Sub
#End Region

#Region "Appearance"
    Public Property CustomAppearance(ByVal index As Integer) As Integer
        Get
            Return MyCustomAppearance(index)
        End Get
        Set(value As Integer)
            MyCustomAppearance(index) = value
            Select Case index
                Case 0
                    For i As Integer = 0 To Count - 1
                        Appearances(i).TranslateXEnabled = value
                        Appearances(i).TranslateYEnabled = value
                        Appearances(i).TranslateZEnabled = value
                    Next
                Case 1
                    For i As Integer = 0 To Count - 1
                        Appearances(i).TranslateXYEnabled = value
                        Appearances(i).TranslateYZEnabled = value
                        Appearances(i).TranslateZXEnabled = value
                    Next
                Case 2
                    For i As Integer = 0 To Count - 1
                        Appearances(i).FreeTranslate = If(value, 2, 0)
                    Next
                Case 3
                    For i As Integer = 0 To Count - 1
                        Appearances(i).RotateXEnabled = value
                        Appearances(i).RotateYEnabled = value
                        Appearances(i).RotateZEnabled = value
                    Next
                Case 4
                    For i As Integer = 0 To Count - 1
                        Appearances(i).ScaleXEnabled = value
                        Appearances(i).ScaleYEnabled = value
                        Appearances(i).ScaleZEnabled = value
                    Next
                Case 5
                    For i As Integer = 0 To Count - 1
                        Appearances(i).Radius = value
                    Next
                Case 6
                    For i As Integer = 0 To Count - 1
                        Appearances(i).ArrowHeadLength = value * 2
                        Appearances(i).ArrowHeadWidth = value
                    Next
                Case 7
                    For i As Integer = 0 To Count - 1
                        Appearances(i).ArcThickness = value
                        Appearances(i).AxisThickness = value
                    Next
                Case 8
                    For i As Integer = 0 To Count - 1
                        Appearances(i).PlanarTranslationGripSize = If(MyCustomAppearance(1), value, 0)
                    Next
                Case 9
                    For i As Integer = 0 To Count - 1
                        Appearances(i).PlanarTranslationGripCorner = If(MyCustomAppearance(1), value, 0)
                    Next
                Case Else
                    Throw New ArgumentOutOfRangeException()
            End Select
            ChangeAppearances()
        End Set
    End Property

    Public Property CustomAppearance As Integer()
        Get
            Return MyCustomAppearance
        End Get
        Set(value As Integer())
            If value Is Nothing Then Return
            If GumballComp.AppearancePresetsEqual(value, MyCustomAppearance) Then Return
            MyCustomAppearance = value

            For i As Int32 = 0 To Count - 1
                'Appearance.
                Dim app As Rhino.UI.Gumball.GumballAppearanceSettings = Appearances(i)
                app.MenuEnabled = False

                'Translate.
                app.TranslateXEnabled = MyCustomAppearance(0)
                app.TranslateYEnabled = MyCustomAppearance(0)
                app.TranslateZEnabled = MyCustomAppearance(0)
                'Free translate.
                If (MyCustomAppearance(2)) Then
                    app.FreeTranslate = 2
                Else
                    app.FreeTranslate = 0
                End If
                'Rotate.
                app.RotateXEnabled = MyCustomAppearance(3)
                app.RotateYEnabled = MyCustomAppearance(3)
                app.RotateZEnabled = MyCustomAppearance(3)
                'Scale.
                app.ScaleXEnabled = MyCustomAppearance(4)
                app.ScaleYEnabled = MyCustomAppearance(4)
                app.ScaleZEnabled = MyCustomAppearance(4)
                'Radius.
                app.Radius = MyCustomAppearance(5)
                'Head.
                app.ArrowHeadLength = MyCustomAppearance(6) * 2
                app.ArrowHeadWidth = MyCustomAppearance(6)
                'Thickness.
                app.AxisThickness = MyCustomAppearance(7)
                app.ArcThickness = MyCustomAppearance(7)
                'Planar translate.
                If MyCustomAppearance(1) Then
                    app.TranslateXYEnabled = True
                    app.TranslateYZEnabled = True
                    app.TranslateZXEnabled = True
                    'Plane size.
                    app.PlanarTranslationGripSize = MyCustomAppearance(8)
                    'Plane distance.
                    app.PlanarTranslationGripCorner = MyCustomAppearance(9)
                Else
                    app.TranslateXYEnabled = False
                    app.TranslateYZEnabled = False
                    app.TranslateZXEnabled = False
                    'Plane size.
                    app.PlanarTranslationGripSize = 0
                    'Plane distance.
                    app.PlanarTranslationGripCorner = 0
                End If

                If (Geometry(i).ObjectType = Rhino.DocObjects.ObjectType.Point) Then
                    app.ScaleXEnabled = False
                    app.ScaleYEnabled = False
                    app.ScaleZEnabled = False
                End If

                Appearances(i) = app
            Next

            ChangeAppearances()
        End Set
    End Property

    Friend Function IsLiveGripDragActive() As Boolean
        Return Component IsNot Nothing AndAlso Component.LiveTransformsWhileDragging AndAlso
            PreviewGripSlot >= 0 AndAlso Not (PreviewGripDelta = Transform.Identity)
    End Function

    ''' <summary>Grip clicked for numeric entry; PickResult.Mode must survive solves while Live is on.</summary>
    Friend Function IsNumericGripPickActive() As Boolean
        Return Index >= 0 AndAlso Index < Count AndAlso Not _gripExceededDragThreshold
    End Function

    ''' <summary>Active viewport grip drag or numeric entry — defer gumball dispose/resync until finished.</summary>
    Friend Function IsGripInteractionActive() As Boolean
        Return TextBox IsNot Nothing OrElse (Index >= 0 AndAlso Index < Count)
    End Function

    Private Sub ChangeAppearances()
        RefreshConduitDisplays()
    End Sub
#End Region

#Region "Hover highlight"

    Private _hoverAppearanceSlot As Integer = -1
    Private _hoverAppearanceMode As Rhino.UI.Gumball.GumballMode = Rhino.UI.Gumball.GumballMode.None
    ''' <summary>Draws only the hovered grip in black on top of the normal gumball.</summary>
    Private _hoverHighlightConduit As Rhino.UI.Gumball.GumballDisplayConduit
    ''' <summary>Hit-tests grips without disturbing the visible gumball conduits.</summary>
    Private _hoverPickConduit As Rhino.UI.Gumball.GumballDisplayConduit
    Private _hoverTimer As Timer
    Private _hoverPollActive As Boolean
    Private _hoverPollHooked As Boolean
    Private _lastHoverScreenPt As Drawing.Point
    Private _hookedCanvas As GH_Canvas

    Private Structure HoverPick
        Public Slot As Integer
        Public Mode As Rhino.UI.Gumball.GumballMode
    End Structure

    ''' <returns>True when hover-poll listening state changed.</returns>
    Friend Function SyncHoverPollIfNeeded(active As Boolean) As Boolean
        Dim want As Boolean = active AndAlso Me.Enabled
        If want = _hoverPollActive Then Return False
        _hoverPollActive = want
        If _hoverPollActive Then
            EnsureHoverTimer()
            _hoverTimer.Enabled = True
            AttachGhCanvasHoverHook()
            _lastHoverScreenPt = New Drawing.Point(Integer.MinValue, Integer.MinValue)
            PollHoverFromGlobalCursor()
        Else
            If _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = False
            DetachGhCanvasHoverHook()
            If Component IsNot Nothing Then Component.SetHoverTarget(-1, Rhino.UI.Gumball.GumballMode.None)
        End If
        Return True
    End Function

    Private Sub EnsureHoverTimer()
        If _hoverTimer IsNot Nothing Then Return
        _hoverTimer = New Timer With {.Interval = 40}
        AddHandler _hoverTimer.Tick, AddressOf OnHoverPollTick
    End Sub

    Private Sub OnHoverPollTick(sender As Object, e As EventArgs)
        PollHoverFromGlobalCursor()
    End Sub

    Private Sub AttachGhCanvasHoverHook()
        If _hoverPollHooked Then Return
        Dim cv As GH_Canvas = TryResolveGrasshopperCanvas()
        If cv Is Nothing Then Return
        _hookedCanvas = cv
        AddHandler _hookedCanvas.MouseMove, AddressOf GhCanvas_MouseMoveHover
        _hoverPollHooked = True
    End Sub

    Private Sub DetachGhCanvasHoverHook()
        If Not _hoverPollHooked OrElse _hookedCanvas Is Nothing Then
            _hookedCanvas = Nothing
            _hoverPollHooked = False
            Return
        End If
        RemoveHandler _hookedCanvas.MouseMove, AddressOf GhCanvas_MouseMoveHover
        _hookedCanvas = Nothing
        _hoverPollHooked = False
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
        If Not _hoverPollActive OrElse Component Is Nothing OrElse TextBox IsNot Nothing Then Return
        If Index >= 0 Then Return
        Try
            Dim screenPt As Drawing.Point = Control.MousePosition
            If screenPt = _lastHoverScreenPt Then Return
            _lastHoverScreenPt = screenPt

            Dim ghDoc As GH_Document = Component.OnPingDocument()
            Dim targetDoc As Rhino.RhinoDoc = If(ghDoc IsNot Nothing, ghDoc.RhinoDocument, Nothing)
            If targetDoc Is Nothing Then targetDoc = Rhino.RhinoDoc.ActiveDoc
            If targetDoc Is Nothing Then
                Component.SetHoverTarget(-1, Rhino.UI.Gumball.GumballMode.None)
                Return
            End If

            For Each view As Rhino.Display.RhinoView In targetDoc.Views
                If view Is Nothing Then Continue For
                If view.Document IsNot targetDoc Then Continue For
                Dim rect As Drawing.Rectangle = view.ScreenRectangle
                If rect.Width <= 0 OrElse rect.Height <= 0 Then Continue For
                If Not rect.Contains(screenPt) Then Continue For
                Dim clientPt As Drawing.Point = view.ScreenToClient(screenPt)
                Dim hit As HoverPick = PickHoverTarget(view, clientPt)
                Component.SetHoverTarget(hit.Slot, hit.Mode)
                Return
            Next
            Component.SetHoverTarget(-1, Rhino.UI.Gumball.GumballMode.None)
        Catch
        End Try
    End Sub

    Private Sub ResumeHoverPoll()
        If _hoverPollActive AndAlso _hoverTimer IsNot Nothing Then _hoverTimer.Enabled = True
        _lastHoverScreenPt = New Drawing.Point(Integer.MinValue, Integer.MinValue)
        PollHoverFromGlobalCursor()
    End Sub

    Private Function PickHoverTarget(view As Rhino.Display.RhinoView, viewportPt As Drawing.Point) As HoverPick
        Dim result As HoverPick
        result.Slot = -1
        result.Mode = Rhino.UI.Gumball.GumballMode.None
        If Component Is Nothing OrElse view Is Nothing OrElse Conduits Is Nothing OrElse Gumballs Is Nothing OrElse Appearances Is Nothing Then Return result
        EnsureHoverPickConduit()
        Dim pick As New Rhino.Input.Custom.PickContext
        pick.View = view
        pick.PickStyle = Rhino.Input.Custom.PickStyle.PointPick
        Dim vp As RhinoViewport = view.ActiveViewport
        If vp Is Nothing Then Return result
        pick.SetPickTransform(vp.GetPickTransform(viewportPt))
        Dim pickline As Line = Nothing
        vp.GetFrustumLine(CDbl(viewportPt.X), CDbl(viewportPt.Y), pickline)
        pick.PickLine = pickline
        pick.UpdateClippingPlanes()

        For i As Integer = 0 To Count - 1
            If Component.SlotSettings IsNot Nothing AndAlso i < Component.SlotSettings.Length AndAlso Not Component.SlotSettings(i).Active Then Continue For
            If Not Component.WantsSlotVisible(i) Then Continue For
            If Not Conduits(i).Enabled Then Continue For
            _hoverPickConduit.SetBaseGumball(Gumballs(i), Appearances(i))
            _hoverPickConduit.PreTransform = Conduits(i).PreTransform
            If _hoverPickConduit.PickGumball(pick, Nothing) Then
                result.Slot = i
                result.Mode = _hoverPickConduit.PickResult.Mode
                _hoverPickConduit.PickResult.SetToDefault()
                Return result
            End If
        Next
        Return result
    End Function

    Friend Sub ApplyHoverAppearance(slot As Integer, mode As Rhino.UI.Gumball.GumballMode)
        If slot = _hoverAppearanceSlot AndAlso mode = _hoverAppearanceMode Then Return
        _hoverAppearanceSlot = slot
        _hoverAppearanceMode = mode

        If slot < 0 OrElse slot >= Count OrElse mode = Rhino.UI.Gumball.GumballMode.None OrElse
            Appearances Is Nothing OrElse Gumballs Is Nothing OrElse Conduits Is Nothing Then
            ClearHoverHighlight()
            Return
        End If

        EnsureHoverHighlightConduit()
        Dim hoverApp As Rhino.UI.Gumball.GumballAppearanceSettings = CopyAppearanceSettings(Appearances(slot))
        DisableAllGrips(hoverApp)
        ConfigureHoverHighlightAppearance(hoverApp, mode)

        Try
            Dim vis As Boolean = Conduits(slot).Enabled AndAlso
                Component IsNot Nothing AndAlso Component.WantsSlotVisible(slot)
            _hoverHighlightConduit.SetBaseGumball(Gumballs(slot), hoverApp)
            _hoverHighlightConduit.PreTransform = Conduits(slot).PreTransform
            _hoverHighlightConduit.Enabled = vis
        Catch
            ClearHoverHighlight()
        End Try
    End Sub

    Private Sub EnsureHoverPickConduit()
        If _hoverPickConduit IsNot Nothing Then Return
        _hoverPickConduit = New Rhino.UI.Gumball.GumballDisplayConduit()
        _hoverPickConduit.Enabled = False
    End Sub

    Private Sub EnsureHoverHighlightConduit()
        If _hoverHighlightConduit IsNot Nothing Then Return
        _hoverHighlightConduit = New Rhino.UI.Gumball.GumballDisplayConduit()
        _hoverHighlightConduit.Enabled = False
    End Sub

    Private Sub ClearHoverHighlight()
        If _hoverHighlightConduit IsNot Nothing Then _hoverHighlightConduit.Enabled = False
    End Sub

    Private Shared Function CopyAppearanceSettings(src As Rhino.UI.Gumball.GumballAppearanceSettings) As Rhino.UI.Gumball.GumballAppearanceSettings
        Dim dst As New Rhino.UI.Gumball.GumballAppearanceSettings()
        If src Is Nothing Then Return dst
        dst.RelocateEnabled = src.RelocateEnabled
        dst.MenuEnabled = src.MenuEnabled
        dst.TranslateXEnabled = src.TranslateXEnabled
        dst.TranslateYEnabled = src.TranslateYEnabled
        dst.TranslateZEnabled = src.TranslateZEnabled
        dst.TranslateXYEnabled = src.TranslateXYEnabled
        dst.TranslateYZEnabled = src.TranslateYZEnabled
        dst.TranslateZXEnabled = src.TranslateZXEnabled
        dst.RotateXEnabled = src.RotateXEnabled
        dst.RotateYEnabled = src.RotateYEnabled
        dst.RotateZEnabled = src.RotateZEnabled
        dst.ScaleXEnabled = src.ScaleXEnabled
        dst.ScaleYEnabled = src.ScaleYEnabled
        dst.ScaleZEnabled = src.ScaleZEnabled
        dst.FreeTranslate = src.FreeTranslate
        dst.ColorX = src.ColorX
        dst.ColorY = src.ColorY
        dst.ColorZ = src.ColorZ
        dst.ColorMenuButton = src.ColorMenuButton
        dst.Radius = src.Radius
        dst.ArrowHeadLength = src.ArrowHeadLength
        dst.ArrowHeadWidth = src.ArrowHeadWidth
        dst.ScaleGripSize = src.ScaleGripSize
        dst.PlanarTranslationGripCorner = src.PlanarTranslationGripCorner
        dst.PlanarTranslationGripSize = src.PlanarTranslationGripSize
        dst.AxisThickness = src.AxisThickness
        dst.ArcThickness = src.ArcThickness
        dst.MenuDistance = src.MenuDistance
        dst.MenuSize = src.MenuSize
        Return dst
    End Function

    Private Shared Sub DisableAllGrips(app As Rhino.UI.Gumball.GumballAppearanceSettings)
        If app Is Nothing Then Return
        app.RelocateEnabled = False
        app.MenuEnabled = False
        app.TranslateXEnabled = False
        app.TranslateYEnabled = False
        app.TranslateZEnabled = False
        app.TranslateXYEnabled = False
        app.TranslateYZEnabled = False
        app.TranslateZXEnabled = False
        app.RotateXEnabled = False
        app.RotateYEnabled = False
        app.RotateZEnabled = False
        app.ScaleXEnabled = False
        app.ScaleYEnabled = False
        app.ScaleZEnabled = False
        app.FreeTranslate = 0
    End Sub

    Private Shared Sub ConfigureHoverHighlightAppearance(app As Rhino.UI.Gumball.GumballAppearanceSettings,
                                                        mode As Rhino.UI.Gumball.GumballMode)
        If app Is Nothing Then Return
        Select Case mode
            Case Rhino.UI.Gumball.GumballMode.TranslateX
                app.TranslateXEnabled = True
                app.ColorX = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateY
                app.TranslateYEnabled = True
                app.ColorY = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateZ
                app.TranslateZEnabled = True
                app.ColorZ = Color.Black
            Case Rhino.UI.Gumball.GumballMode.RotateX
                app.RotateXEnabled = True
                app.ColorX = Color.Black
            Case Rhino.UI.Gumball.GumballMode.RotateY
                app.RotateYEnabled = True
                app.ColorY = Color.Black
            Case Rhino.UI.Gumball.GumballMode.RotateZ
                app.RotateZEnabled = True
                app.ColorZ = Color.Black
            Case Rhino.UI.Gumball.GumballMode.ScaleX
                app.ScaleXEnabled = True
                app.ColorX = Color.Black
            Case Rhino.UI.Gumball.GumballMode.ScaleY
                app.ScaleYEnabled = True
                app.ColorY = Color.Black
            Case Rhino.UI.Gumball.GumballMode.ScaleZ
                app.ScaleZEnabled = True
                app.ColorZ = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateXY
                app.TranslateXEnabled = True
                app.TranslateYEnabled = True
                app.TranslateXYEnabled = True
                app.ColorX = Color.Black
                app.ColorY = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateYZ
                app.TranslateYEnabled = True
                app.TranslateZEnabled = True
                app.TranslateYZEnabled = True
                app.ColorY = Color.Black
                app.ColorZ = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateZX
                app.TranslateZEnabled = True
                app.TranslateXEnabled = True
                app.TranslateZXEnabled = True
                app.ColorZ = Color.Black
                app.ColorX = Color.Black
            Case Rhino.UI.Gumball.GumballMode.TranslateFree
                app.FreeTranslate = 2
                app.ColorX = Color.Black
                app.ColorY = Color.Black
                app.ColorZ = Color.Black
            Case Rhino.UI.Gumball.GumballMode.Menu
                app.MenuEnabled = True
                app.ColorMenuButton = Color.Black
        End Select
    End Sub

#End Region

#Region "Serializable"

    Public Function GumballWriter(ByVal writer As GH_IO.Serialization.GH_IWriter) As Boolean
        If Not IsRuntimeStateCompleteForSerialization() Then Return True

        'gbroot 
        '   |
        '   ├─gbdata
        '   |     |
        '   |     ├─countgeo
        '   |     |       |
        '   |     |       └─count
        '   |     |
        '   |     ├─geometry
        '   |     |       |
        '   |     |       ├─geo
        '   |     |      (i)           
        '   |     | 
        '   |     ├─transform
        '   |     |       |
        '   |     |       ├─gh_transform
        '   |     |      (i)
        '   |     |  
        '   |     └─gumball
        '   |             |
        '   |             ├─frameplane
        '   |             ├─scalegripdistance
        '   |            (i)
        '   | 
        '   └─gbattributes 
        '         |
        '         ├─gumballattributes_values
        '         |                     |
        '         |                     ├─GbAtt_Translate
        '         |                     ├─GbAtt_PlanarTranslate
        '         |                     ├─GbAtt_FreeTranslate
        '         |                     ├─GbAtt_Rotate
        '         |                     ├─GbAtt_Scale
        '         |                     ├─GbAtt_Radius
        '         |                     ├─GbAtt_ArrowHead
        '         |                     ├─GbAtt_Thickness
        '         |                     ├─GbAtt_PlaneSize
        '         |                     └─GbAtt_PlaneDistance
        '         | 
        '         └─gumballattributes_modes
        '                               |
        '                               ├─valmode
        '                               ├─attmode
        '                               ├─aligntogeometry
        '                               ├─preservexf
        '                               ├─proximitycache
        '                               ├─livetransform
        '                               └─snaptogeometry

        Try
            Dim i As New Integer

            'Root.
            ' writer.RemoveChunk("gbroot")
            Dim root As GH_IWriter = writer.CreateChunk("gbroot")

            'Data.
            Dim data As GH_IWriter = root.CreateChunk("gbdata", 0)

            'Count.
            Dim co As GH_IWriter = data.CreateChunk("countgeo", 0)
            co.SetInt32("count", 0, Me.Count)

            'Geometry.
            Dim geo As GH_IWriter = data.CreateChunk("geometry", 1)
            For i = 0 To Count - 1
                Dim bytes As Byte() = GH_Convert.CommonObjectToByteArray(Geometry(i))
                geo.SetByteArray("geo", i, bytes)
            Next

            'Transform.
            Dim xf As GH_IWriter = data.CreateChunk("transform", 2)
            For i = 0 To Count - 1
                Dim t As GH_IWriter = xf.CreateChunk("gh_transform", i)
                Xform(i).Write(t)
            Next

            'Gumball.
            Dim obj As GH_IWriter = data.CreateChunk("gumball", 3)
            For i = 0 To Count - 1
                Dim frame As Plane = Gumballs(i).Frame.Plane
                Dim pln As GH_IO.Types.GH_Plane
                pln.Origin = New GH_IO.Types.GH_Point3D(frame.Origin.X, frame.Origin.Y, frame.Origin.Z)
                pln.XAxis = New GH_IO.Types.GH_Point3D(frame.XAxis.X, frame.XAxis.Y, frame.XAxis.Z)
                pln.YAxis = New GH_IO.Types.GH_Point3D(frame.YAxis.X, frame.YAxis.Y, frame.YAxis.Z)
                obj.SetPlane("frameplane", i, pln)
                Dim scd As Vector3d = Gumballs(i).Frame.ScaleGripDistance
                Dim vec As New GH_IO.Types.GH_Point3D(scd.X, scd.Y, scd.Z)
                obj.SetPoint3D("scalegripdistance", i, vec)
            Next

            'Attributes.
            Dim att As GH_IWriter = root.CreateChunk("gbattributes", 1)

            'Attributes_values
            Dim att0 As GH_IWriter = att.CreateChunk("gumballattributes_values", 0)
            att0.SetInt32("GbAtt_Translate", 0, MyCustomAppearance(0))
            att0.SetInt32("GbAtt_PlanarTranslate", 1, MyCustomAppearance(1))
            att0.SetInt32("GbAtt_FreeTranslate", 2, MyCustomAppearance(2))
            att0.SetInt32("GbAtt_Rotate", 3, MyCustomAppearance(3))
            att0.SetInt32("GbAtt_Scale", 4, MyCustomAppearance(4))
            att0.SetInt32("GbAtt_Radius", 5, MyCustomAppearance(5))
            att0.SetInt32("GbAtt_ArrowHead", 6, MyCustomAppearance(6))
            att0.SetInt32("GbAtt_Thickness", 7, MyCustomAppearance(7))
            att0.SetInt32("GbAtt_PlaneSize", 8, MyCustomAppearance(8))
            att0.SetInt32("GbAtt_PlaneDistance", 9, MyCustomAppearance(9))

            'Attributes_modes
            Dim att1 As GH_IWriter = att.CreateChunk("gumballattributes_modes", 1)
            att1.SetInt32("valmode", 0, Me.Component.ModeValue(0))
            att1.SetInt32("attmode", 1, Me.Component.ModeValue(1))
            att1.SetInt32("displaymode", 7, Me.Component.ModeValue(1))
            att1.SetBoolean("aligntogeometry", 2, Me.Component.ModeValue(2))
            att1.SetBoolean("preservexf", 3, Me.Component.PreserveTransformsOnGeometryChange)
            att1.SetBoolean("proximitycache", 4, Me.Component.ProximityCache)
            att1.SetBoolean("saveshifted", 9, Me.Component.ProximityCache)
            att1.SetBoolean("livetransform", 5, Me.Component.LiveTransformsWhileDragging)
            att1.SetBoolean("snaptogeometry", 6, CBool(Me.Component.ModeValue(3)))
            If Not Double.IsNaN(Me.Component.SnapTranslateTolerance) AndAlso Me.Component.SnapTranslateTolerance > 0 Then
                att1.SetDouble("snaptol", 8, Me.Component.SnapTranslateTolerance)
            End If

        Catch ex As Exception
            Rhino.RhinoApp.WriteLine("WRITER_GB; " & ex.ToString())
        End Try
        Return True
    End Function

    Public Function GumballReader(Reader As GH_IO.Serialization.GH_IReader) As Boolean

        If Not GhClipboardRootLooksComplete(Reader) Then
            Return False
            Exit Function
        End If

        Try
            Dim i As New Integer

            'Root.
            Dim root As GH_IReader = Reader.FindChunk("gbroot")

            'Data.
            Dim data As GH_IReader = root.FindChunk("gbdata", 0)

            'Count.
            Dim countgeo As GH_IReader = data.FindChunk("countgeo", 0)
            Count = countgeo.GetInt32("count", 0)
            ReDim SlotAlignStates(Count - 1)

            'Geomtry.
            Geometry = New GeometryBase(Count - 1) {}
            Dim g As GH_IO.Serialization.GH_IReader = data.FindChunk("geometry", 1)
            For i = 0 To Count - 1
                Dim bytes As Byte() = g.GetByteArray("geo", i)
                Geometry(i) = GH_Convert.ByteArrayToCommonObject(Of GeometryBase)(bytes)
            Next

            'Transform.
            Xform = New Types.GH_Transform(Count - 1) {}
            Dim xf As GH_IO.Serialization.GH_IReader = data.FindChunk("transform", 2)
            For i = 0 To Count - 1
                Dim t As GH_IO.Serialization.GH_IReader = xf.FindChunk("gh_transform", i)
                Dim ghxform As New Types.GH_Transform()
                ghxform.Read(t)
                Xform(i) = ghxform
            Next

            'Gumball.
            Gumballs = New Rhino.UI.Gumball.GumballObject(Count - 1) {}
            Dim go As GH_IO.Serialization.GH_IReader = data.FindChunk("gumball", 3)
            For i = 0 To Count - 1
                Dim gb As New Rhino.UI.Gumball.GumballObject
                Dim frame As New Rhino.UI.Gumball.GumballFrame
                Dim pln As GH_IO.Types.GH_Plane = go.GetPlane("frameplane", i)
                frame.Plane = New Plane(New Point3d(pln.Origin.x, pln.Origin.y, pln.Origin.z), New Vector3d(pln.XAxis.x, pln.XAxis.y, pln.XAxis.z), New Vector3d(pln.YAxis.x, pln.YAxis.y, pln.YAxis.z))
                Dim scd As GH_IO.Types.GH_Point3D = go.GetPoint3D("scalegripdistance", i)
                frame.ScaleGripDistance = New Vector3d(scd.x, scd.y, scd.z)
                gb.Frame = frame
                Gumballs(i) = gb
            Next

            'Attributes.
            Dim att As GH_IReader = root.FindChunk("gbattributes", 1)

            'Attributes_values
            Dim att0 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_values", 0)

            MyCustomAppearance(0) = att0.GetInt32("GbAtt_Translate", 0)
            MyCustomAppearance(1) = att0.GetInt32("GbAtt_PlanarTranslate", 1)
            MyCustomAppearance(2) = att0.GetInt32("GbAtt_FreeTranslate", 2)
            MyCustomAppearance(3) = att0.GetInt32("GbAtt_Rotate", 3)
            MyCustomAppearance(4) = att0.GetInt32("GbAtt_Scale", 4)
            MyCustomAppearance(5) = att0.GetInt32("GbAtt_Radius", 5)
            MyCustomAppearance(6) = att0.GetInt32("GbAtt_ArrowHead", 6)
            MyCustomAppearance(7) = att0.GetInt32("GbAtt_Thickness", 7)
            MyCustomAppearance(8) = att0.GetInt32("GbAtt_PlaneSize", 8)
            MyCustomAppearance(9) = att0.GetInt32("GbAtt_PlaneDistance", 9)

            'Attributes_modes
            Dim att1 As GH_IO.Serialization.GH_Chunk = att.FindChunk("gumballattributes_modes", 1)
            Component.ModeValue(0) = att1.GetInt32("valmode", 0)
            Component.ModeValue(1) = GumballComp.LoadDisplayModeFromChunk(att1)
            Dim alignF2 As Boolean = att1.GetBoolean("aligntogeometry", 2)
            Dim snapF2 As Boolean = False
            If Not att1.TryGetBoolean("snaptogeometry", 6, snapF2) Then snapF2 = False
            Component.ApplyOptionalInputModesFromFile(alignF2, snapF2)
            Dim snapTolStored As Double
            If att1.TryGetDouble("snaptol", 8, snapTolStored) AndAlso snapTolStored > 0 Then
                Component.SnapTranslateTolerance = snapTolStored
            End If
            Component.PreserveTransformsOnGeometryChange = att1.GetBoolean("preservexf", 3)
            Dim proxStored2 As Boolean
            If att1.TryGetBoolean("proximitycache", 4, proxStored2) Then
                Component.ProximityCache = proxStored2
            Else
                Component.ProximityCache = False
            End If
            Dim discardSs2 As Boolean
            att1.TryGetBoolean("saveshifted", 9, discardSs2)
            Component.SaveShifted = Component.ProximityCache
            Dim lvStored2 As Boolean
            If att1.TryGetBoolean("livetransform", 5, lvStored2) Then
                Component.LiveTransformsWhileDragging = lvStored2
            Else
                Component.LiveTransformsWhileDragging = False
            End If

            'End reader.

            For i = 0 To Count - 1
                'Appearance.
                Dim app As Rhino.UI.Gumball.GumballAppearanceSettings = Appearances(i)
                app.MenuEnabled = False

                'Translate.
                app.TranslateXEnabled = MyCustomAppearance(0)
                app.TranslateYEnabled = MyCustomAppearance(0)
                app.TranslateZEnabled = MyCustomAppearance(0)
                'Free translate.
                If (MyCustomAppearance(2)) Then
                    app.FreeTranslate = 2
                Else
                    app.FreeTranslate = 0
                End If
                'Rotate.
                app.RotateXEnabled = MyCustomAppearance(3)
                app.RotateYEnabled = MyCustomAppearance(3)
                app.RotateZEnabled = MyCustomAppearance(3)
                'Scale.
                app.ScaleXEnabled = MyCustomAppearance(4)
                app.ScaleYEnabled = MyCustomAppearance(4)
                app.ScaleZEnabled = MyCustomAppearance(4)
                'Radius.
                app.Radius = MyCustomAppearance(5)
                'Head.
                app.ArrowHeadLength = MyCustomAppearance(6) * 2
                app.ArrowHeadWidth = MyCustomAppearance(6)
                'Thickness.
                app.AxisThickness = MyCustomAppearance(7)
                app.ArcThickness = MyCustomAppearance(7)
                'Planar translate.
                If MyCustomAppearance(1) Then
                    app.TranslateXYEnabled = True
                    app.TranslateYZEnabled = True
                    app.TranslateZXEnabled = True
                    'Plane size.
                    app.PlanarTranslationGripSize = MyCustomAppearance(8)
                    'Plane distance.
                    app.PlanarTranslationGripCorner = MyCustomAppearance(9)
                Else
                    app.TranslateXYEnabled = False
                    app.TranslateYZEnabled = False
                    app.TranslateZXEnabled = False
                    'Plane size.
                    app.PlanarTranslationGripSize = 0
                    'Plane distance.
                    app.PlanarTranslationGripCorner = 0
                End If

                If (Geometry(i).ObjectType = Rhino.DocObjects.ObjectType.Point) Then
                    app.ScaleXEnabled = False
                    app.ScaleYEnabled = False
                    app.ScaleZEnabled = False
                End If

                Appearances(i) = app

                'Display conduit.
                Conduits(i).SetBaseGumball(Gumballs(i), app)

                Me.Component.ExpireSolution(True)
                If (Me.Component.WantsSlotVisible(i)) Then Me.Component.SyncGumballVisibility()

            Next
        Catch ex As Exception
            Rhino.RhinoApp.WriteLine("READER_GB; " & ex.ToString())
            ClearAfterFailedOrEmptyDeserialize()
            Return False
        End Try
        Return IsRuntimeStateCompleteForSerialization()
    End Function
    '
    '
    Public Function Write(writer As GH_IWriter) As Boolean Implements GH_ISerializable.Write
        GumballWriter(writer)
        Return True
    End Function

    Public Function Read(reader As GH_IReader) As Boolean Implements GH_ISerializable.Read
        GumballReader(reader)
        Return True
    End Function
#End Region

End Class

Public Class GbUndo
    Inherits Grasshopper.Kernel.Undo.GH_ArchivedUndoAction

    Private GB As GhGumball

    Sub New(MyGb As GhGumball)
        GB = MyGb
        Me.m_data = Me.SerializeToByteArray(MyGb)
        Dim chunk As New GH_LooseChunk("GbUndo")
        Me.Write(chunk)
    End Sub

    Protected Overrides Sub Internal_Redo(doc As GH_Document)
        Internal_Undo(doc)
    End Sub

    Protected Overrides Sub Internal_Undo(doc As GH_Document)
        Dim reader As New GH_LooseChunk("GbUndo")
        reader.Deserialize_Binary(Me.m_data)
        GB.Read(reader)
    End Sub
End Class

Public Class FormAttributes
    Inherits System.Windows.Forms.Form

    ''' <summary>Backdrop click (Rhino viewport) dismissal, same routing as numeric gumball entry.</summary>
    Friend Shared Function ConsumeBackdropMouseDown() As Boolean
        Dim f As FormAttributes = _activeInstance
        If f Is Nothing OrElse f.IsDisposed OrElse Not f.Visible Then Return False
        If f._committing OrElse Not f._outsideDismissReady Then Return False
        If Environment.TickCount < f._suppressBackdropDismissUntil Then Return False
        f.TryDismissFromOutsideRhinoGesture()
        Return True
    End Function

    Friend Shared Sub RequestDismissFromBackdropMouse()
        ConsumeBackdropMouseDown()
    End Sub

    Private Shared _activeInstance As FormAttributes
    Private _committing As Boolean
    Private _outsideDismissReady As Boolean
    Private _suppressBackdropDismissUntil As Integer
    Private _hookedCanvas As GH_Canvas

    Private Component As GumballComp
    Private CanSend As Boolean

    Sub New(Comp As GumballComp)
        CloseStaleAttributesFloat()
        Component = Comp
        _activeInstance = Me
        InitializeComponent()
        PositionAttributesFormOnGrasshopperHost()
        GumballNumericBackdropMouse.Instance.EnsureEnabled()
        _suppressBackdropDismissUntil = Environment.TickCount + 1200
        Dim ownerForm As Form = TryCast(Grasshopper.Instances.DocumentEditor, Form)
        If ownerForm IsNot Nothing Then
            Show(ownerForm)
        Else
            Show()
        End If
    End Sub

    ''' <summary>Anchor the Attributes window to the Grasshopper editor so it stacks above Rhino reliably (macOS/WPF hosts).</summary>
    Private Sub PositionAttributesFormOnGrasshopperHost()
        Try
            Dim host As Control = TryCast(Grasshopper.Instances.DocumentEditor, Control)
            If host Is Nothing Then
                Me.StartPosition = FormStartPosition.CenterScreen
                Return
            End If
            Me.StartPosition = FormStartPosition.Manual
            Dim client As Drawing.Rectangle = host.RectangleToScreen(New Drawing.Rectangle(0, 0, host.ClientSize.Width, host.ClientSize.Height))
            Dim sr As Drawing.Rectangle = Screen.FromRectangle(client).WorkingArea
            Dim lx As Integer = client.Left + client.Width \ 2 - Me.Width \ 2
            Dim ly As Integer = client.Top + Math.Min(CInt(client.Height / 10.0F), 140)
            lx = Math.Max(sr.Left + 10, Math.Min(lx, sr.Right - Me.Width - 10))
            ly = Math.Max(sr.Top + 10, Math.Min(ly, sr.Bottom - Me.Height - 10))
            Me.Location = New Drawing.Point(lx, ly)
        Catch
            Me.StartPosition = FormStartPosition.CenterScreen
        End Try
    End Sub

    Private Shared Sub CloseStaleAttributesFloat()
        If (_activeInstance Is Nothing OrElse _activeInstance.IsDisposed) Then Return
        Try
            _activeInstance.RequestDismiss()
        Catch
            Try
                _activeInstance.Close()
            Catch
            End Try
        End Try
    End Sub

    Friend Sub RequestDismiss()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookAttributes()
        Try
            Close()
        Catch
        End Try
    End Sub

    Private Sub TryDismissFromOutsideRhinoGesture()
        If _committing OrElse Not _outsideDismissReady Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        If Not Visible Then Return
        Dim self As FormAttributes = Me
        BeginInvoke(New Action(Sub()
                                   If self._committing Then Return
                                   If Not self.Visible Then Return
                                   self.RequestDismiss()
                               End Sub))
    End Sub

    Private Sub Canvas_MouseDownDismissHookAttributes(sender As Object, e As MouseEventArgs)
        If _committing OrElse Not _outsideDismissReady Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        If e.Button <> MouseButtons.Left Then Return
        Dim cv As GH_Canvas = TryCast(sender, GH_Canvas)
        If cv Is Nothing Then Return
        Dim screenPt As Drawing.Point = cv.PointToScreen(e.Location)
        If Bounds.Contains(screenPt) Then Return
        TryDismissFromOutsideRhinoGesture()
    End Sub

    Private Shared Function TryResolveGrasshopperCanvasAttributes() As GH_Canvas
        Dim cv As GH_Canvas = Grasshopper.Instances.ActiveCanvas
        If cv IsNot Nothing Then Return cv
        Dim ed As Control = TryCast(Grasshopper.Instances.DocumentEditor, Control)
        If ed Is Nothing Then Return Nothing
        Return FindDescendantCanvasAttributes(ed)
    End Function

    Private Shared Function FindDescendantCanvasAttributes(root As Control) As GH_Canvas
        Dim q As GH_Canvas = TryCast(root, GH_Canvas)
        If q IsNot Nothing Then Return q
        For Each ch As Control In root.Controls
            Dim n As GH_Canvas = FindDescendantCanvasAttributes(ch)
            If n IsNot Nothing Then Return n
        Next
        Return Nothing
    End Function

    Private Sub AttachGrasshopperCanvasDismissHookAttributes()
        DetachGrasshopperCanvasDismissHookAttributes()
        Dim cv As GH_Canvas = TryResolveGrasshopperCanvasAttributes()
        If cv Is Nothing Then Return
        _hookedCanvas = cv
        AddHandler _hookedCanvas.MouseDown, AddressOf Canvas_MouseDownDismissHookAttributes
    End Sub

    Private Sub DetachGrasshopperCanvasDismissHookAttributes()
        If (_hookedCanvas Is Nothing) Then Return
        RemoveHandler _hookedCanvas.MouseDown, AddressOf Canvas_MouseDownDismissHookAttributes
        _hookedCanvas = Nothing
    End Sub

#Region "Events"
    Private Shared Sub SafeNumericValue(nud As NumericUpDown, v As Integer)
        Dim d As Decimal = CDec(v)
        If d < nud.Minimum Then d = nud.Minimum
        If d > nud.Maximum Then d = nud.Maximum
        nud.Value = d
    End Sub

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        CanSend = False
        If (Component.MyGumball IsNot Nothing) Then
            Dim ca As GhGumball = Component.MyGumball
            Me.ButtTranslate.Checked = (ca.CustomAppearance(0) <> 0)
            Me.ButtPlane.Checked = (ca.CustomAppearance(1) <> 0)
            Me.ButtFree.Checked = (ca.CustomAppearance(2) <> 0)
            Me.ButtRotate.Checked = (ca.CustomAppearance(3) <> 0)
            Me.ButtScale.Checked = (ca.CustomAppearance(4) <> 0)
            SafeNumericValue(Me.NumRad, ca.CustomAppearance(5))
            SafeNumericValue(Me.NumAH, ca.CustomAppearance(6))
            SafeNumericValue(Me.NumThk, ca.CustomAppearance(7))
            SafeNumericValue(Me.NumPS, ca.CustomAppearance(8))
            SafeNumericValue(Me.NumPD, ca.CustomAppearance(9))
        End If
        Dim st As Double = Component.SnapTranslateTolerance
        If Double.IsNaN(st) OrElse st <= 0 Then
            NumSnapTol.Value = 0D
        Else
            Dim d As Decimal = CDec(st)
            If d > NumSnapTol.Maximum Then d = NumSnapTol.Maximum
            NumSnapTol.Value = d
        End If
        CanSend = True
    End Sub

    Private Sub Form1_FormClosed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If ReferenceEquals(_activeInstance, Me) Then _activeInstance = Nothing
        DetachGrasshopperCanvasDismissHookAttributes()
        If (Component.MyGumball IsNot Nothing) Then Component.RecordUndoEvent("Gumball Attributes", New GbUndo(Component.MyGumball))
        Me.Component.AttForm = Nothing
    End Sub

    Private Sub FormAttributes_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        GumballNumericBackdropMouse.Instance.EnsureEnabled()
        Dim arm As New Timer With {.Interval = 500}
        AddHandler arm.Tick,
            Sub()
                arm.Stop()
                arm.Dispose()
                _outsideDismissReady = True
                AttachGrasshopperCanvasDismissHookAttributes()
            End Sub
        arm.Start()
        BeginInvoke(New Action(Sub()
                                   Try
                                       BringToFront()
                                       Activate()
                                   Catch
                                   End Try
                               End Sub))
    End Sub

    Private Sub FormAttributes_KeyDown(sender As Object, e As KeyEventArgs) Handles MyBase.KeyDown
        If e.KeyCode = Keys.Escape Then
            e.SuppressKeyPress = True
            RequestDismiss()
        End If
    End Sub

    Private Sub ButtTranslate_CheckedChanged(sender As Object, e As EventArgs) Handles ButtTranslate.CheckedChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(0) = Me.ButtTranslate.CheckState.value__
    End Sub

    Private Sub ButtPlane_CheckedChanged(sender As Object, e As EventArgs) Handles ButtPlane.CheckedChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(1) = Me.ButtPlane.CheckState.value__
    End Sub

    Private Sub ButtFree_CheckedChanged(sender As Object, e As EventArgs) Handles ButtFree.CheckedChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(2) = Me.ButtFree.CheckState.value__
    End Sub

    Private Sub ButtRotate_CheckedChanged(sender As Object, e As EventArgs) Handles ButtRotate.CheckedChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(3) = Me.ButtRotate.CheckState.value__
    End Sub

    Private Sub ButtScale_CheckedChanged(sender As Object, e As EventArgs) Handles ButtScale.CheckedChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(4) = Me.ButtScale.CheckState.value__
    End Sub

    Private Sub NumRad_ValueChanged(sender As Object, e As EventArgs) Handles NumRad.ValueChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(5) = CInt(Me.NumRad.Value)
    End Sub

    Private Sub NumAH_ValueChanged(sender As Object, e As EventArgs) Handles NumAH.ValueChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(6) = CInt(Me.NumAH.Value)
    End Sub

    Private Sub NumThk_ValueChanged(sender As Object, e As EventArgs) Handles NumThk.ValueChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(7) = CInt(Me.NumThk.Value)
    End Sub

    Private Sub NumPS_ValueChanged(sender As Object, e As EventArgs) Handles NumPS.ValueChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(8) = CInt(Me.NumPS.Value)
    End Sub

    Private Sub NumPD_ValueChanged(sender As Object, e As EventArgs) Handles NumPD.ValueChanged
        If (Me.Component.MyGumball IsNot Nothing) AndAlso (CanSend) Then Component.MyGumball.CustomAppearance(9) = CInt(Me.NumPD.Value)
    End Sub

    Private Sub NumSnapTol_ValueChanged(sender As Object, e As EventArgs) Handles NumSnapTol.ValueChanged
        If Not CanSend Then Return
        If NumSnapTol.Value <= 0D Then
            Component.SnapTranslateTolerance = Double.NaN
        Else
            Component.SnapTranslateTolerance = CDbl(NumSnapTol.Value)
        End If
        If Component.MyGumball IsNot Nothing Then
            If Double.IsNaN(Component.SnapTranslateTolerance) OrElse Component.SnapTranslateTolerance <= 0 Then
                Component.MyGumball.SnapTranslateRadiusOverride = Double.NaN
            Else
                Component.MyGumball.SnapTranslateRadiusOverride = Component.SnapTranslateTolerance
            End If
        End If
        Component.ExpireSolution(True)
    End Sub
#End Region

#Region "Design"
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing Then
                DetachGrasshopperCanvasDismissHookAttributes()
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
        Me.NumRad = New System.Windows.Forms.NumericUpDown()
        Me.ButtTranslate = New System.Windows.Forms.CheckBox()
        Me.ButtPlane = New System.Windows.Forms.CheckBox()
        Me.ButtRotate = New System.Windows.Forms.CheckBox()
        Me.ButtScale = New System.Windows.Forms.CheckBox()
        Me.LabelRad = New System.Windows.Forms.Label()
        Me.NumAH = New System.Windows.Forms.NumericUpDown()
        Me.LabelArrow = New System.Windows.Forms.Label()
        Me.LabelThk = New System.Windows.Forms.Label()
        Me.NumThk = New System.Windows.Forms.NumericUpDown()
        Me.LabelPS = New System.Windows.Forms.Label()
        Me.NumPS = New System.Windows.Forms.NumericUpDown()
        Me.LabelPD = New System.Windows.Forms.Label()
        Me.NumPD = New System.Windows.Forms.NumericUpDown()
        Me.ButtFree = New System.Windows.Forms.CheckBox()
        Me.NumSnapTol = New System.Windows.Forms.NumericUpDown()
        Me.LabelSnapTol = New System.Windows.Forms.Label()
        CType(Me.NumRad, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumAH, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumThk, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumPS, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumPD, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumSnapTol, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'ButtTranslate
        '
        Me.ButtTranslate.CheckAlign = System.Drawing.ContentAlignment.MiddleRight
        Me.ButtTranslate.Checked = True
        Me.ButtTranslate.CheckState = System.Windows.Forms.CheckState.Checked
        Me.ButtTranslate.Location = New System.Drawing.Point(12, 17)
        Me.ButtTranslate.Name = "ButtTranslate"
        Me.ButtTranslate.Size = New System.Drawing.Size(126, 17)
        Me.ButtTranslate.TabIndex = 1
        Me.ButtTranslate.Text = "Translate enabled"
        Me.ButtTranslate.UseVisualStyleBackColor = True
        '
        'ButtPlane
        '
        Me.ButtPlane.CheckAlign = System.Drawing.ContentAlignment.MiddleRight
        Me.ButtPlane.Checked = True
        Me.ButtPlane.CheckState = System.Windows.Forms.CheckState.Checked
        Me.ButtPlane.Location = New System.Drawing.Point(12, 40)
        Me.ButtPlane.Name = "ButtPlane"
        Me.ButtPlane.Size = New System.Drawing.Size(126, 17)
        Me.ButtPlane.TabIndex = 2
        Me.ButtPlane.Text = "Plane enabled"
        Me.ButtPlane.UseVisualStyleBackColor = True
        '
        'ButtFree
        '
        Me.ButtFree.CheckAlign = System.Drawing.ContentAlignment.MiddleRight
        Me.ButtFree.Checked = True
        Me.ButtFree.ThreeState = False
        Me.ButtFree.CheckState = System.Windows.Forms.CheckState.Checked
        Me.ButtFree.Location = New System.Drawing.Point(12, 63)
        Me.ButtFree.Name = "ButtFree"
        Me.ButtFree.Size = New System.Drawing.Size(126, 17)
        Me.ButtFree.TabIndex = 3
        Me.ButtFree.Text = "Free translate enabled"
        Me.ButtFree.UseVisualStyleBackColor = True
        '
        'ButtRotate
        '
        Me.ButtRotate.CheckAlign = System.Drawing.ContentAlignment.MiddleRight
        Me.ButtRotate.Checked = True
        Me.ButtRotate.CheckState = System.Windows.Forms.CheckState.Checked
        Me.ButtRotate.Location = New System.Drawing.Point(12, 86)
        Me.ButtRotate.Name = "ButtRotate"
        Me.ButtRotate.Size = New System.Drawing.Size(126, 17)
        Me.ButtRotate.TabIndex = 4
        Me.ButtRotate.Text = "Rotate enabled"
        Me.ButtRotate.UseVisualStyleBackColor = True
        '
        'ButtScale
        '
        Me.ButtScale.CheckAlign = System.Drawing.ContentAlignment.MiddleRight
        Me.ButtScale.Checked = True
        Me.ButtScale.CheckState = System.Windows.Forms.CheckState.Checked
        Me.ButtScale.Location = New System.Drawing.Point(12, 109)
        Me.ButtScale.Name = "ButtScale"
        Me.ButtScale.Size = New System.Drawing.Size(126, 17)
        Me.ButtScale.TabIndex = 5
        Me.ButtScale.Text = "Scale enabled"
        Me.ButtScale.UseVisualStyleBackColor = True
        '
        'NumRad
        '
        Me.NumRad.Location = New System.Drawing.Point(90, 132)
        Me.NumRad.Maximum = New Decimal(New Integer() {200, 0, 0, 0})
        Me.NumRad.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumRad.Name = "NumRad"
        Me.NumRad.Size = New System.Drawing.Size(48, 20)
        Me.NumRad.TabIndex = 6
        Me.NumRad.Value = New Decimal(New Integer() {50, 0, 0, 0})
        '
        'NumAH
        '
        Me.NumAH.Location = New System.Drawing.Point(90, 158)
        Me.NumAH.Maximum = New Decimal(New Integer() {50, 0, 0, 0})
        Me.NumAH.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumAH.Name = "NumAH"
        Me.NumAH.Size = New System.Drawing.Size(48, 20)
        Me.NumAH.TabIndex = 7
        Me.NumAH.Value = New Decimal(New Integer() {5, 0, 0, 0})
        '
        'NumThk
        '
        Me.NumThk.Location = New System.Drawing.Point(90, 184)
        Me.NumThk.Maximum = New Decimal(New Integer() {30, 0, 0, 0})
        Me.NumThk.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumThk.Name = "NumThk"
        Me.NumThk.Size = New System.Drawing.Size(48, 20)
        Me.NumThk.TabIndex = 8
        Me.NumThk.Value = New Decimal(New Integer() {2, 0, 0, 0})
        '
        'NumPS
        '
        Me.NumPS.Location = New System.Drawing.Point(90, 210)
        Me.NumPS.Maximum = New Decimal(New Integer() {100, 0, 0, 0})
        Me.NumPS.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumPS.Name = "NumPS"
        Me.NumPS.Size = New System.Drawing.Size(48, 20)
        Me.NumPS.TabIndex = 9
        Me.NumPS.Value = New Decimal(New Integer() {15, 0, 0, 0})
        '
        'NumPD
        '
        Me.NumPD.Location = New System.Drawing.Point(90, 236)
        Me.NumPD.Maximum = New Decimal(New Integer() {200, 0, 0, 0})
        Me.NumPD.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumPD.Name = "NumPD"
        Me.NumPD.Size = New System.Drawing.Size(48, 20)
        Me.NumPD.TabIndex = 10
        Me.NumPD.Value = New Decimal(New Integer() {35, 0, 0, 0})
        '
        'NumSnapTol
        '
        Me.NumSnapTol.DecimalPlaces = 3
        Me.NumSnapTol.Increment = New Decimal(New Integer() {1, 0, 0, 65536})
        Me.NumSnapTol.Location = New System.Drawing.Point(90, 262)
        Me.NumSnapTol.Maximum = New Decimal(New Integer() {100000, 0, 0, 0})
        Me.NumSnapTol.Name = "NumSnapTol"
        Me.NumSnapTol.Size = New System.Drawing.Size(48, 20)
        Me.NumSnapTol.TabIndex = 11
        '
        'LabelSnapTol
        '
        Me.LabelSnapTol.AutoSize = True
        Me.LabelSnapTol.Location = New System.Drawing.Point(12, 264)
        Me.LabelSnapTol.Name = "LabelSnapTol"
        Me.LabelSnapTol.Size = New System.Drawing.Size(72, 13)
        Me.LabelSnapTol.TabIndex = 16
        Me.LabelSnapTol.Text = "Snap tol (0=auto)"
        '
        'LabelRad
        '
        Me.LabelRad.AutoSize = True
        Me.LabelRad.Location = New System.Drawing.Point(12, 134)
        Me.LabelRad.Name = "LabelRad"
        Me.LabelRad.Size = New System.Drawing.Size(40, 13)
        Me.LabelRad.TabIndex = 11
        Me.LabelRad.Text = "Radius"
        '
        'LabelArrow
        '
        Me.LabelArrow.AutoSize = True
        Me.LabelArrow.Location = New System.Drawing.Point(12, 160)
        Me.LabelArrow.Name = "LabelArrow"
        Me.LabelArrow.Size = New System.Drawing.Size(61, 13)
        Me.LabelArrow.TabIndex = 12
        Me.LabelArrow.Text = "Arrow head"
        '
        'LabelThk
        '
        Me.LabelThk.AutoSize = True
        Me.LabelThk.Location = New System.Drawing.Point(12, 186)
        Me.LabelThk.Name = "LabelThk"
        Me.LabelThk.Size = New System.Drawing.Size(56, 13)
        Me.LabelThk.TabIndex = 13
        Me.LabelThk.Text = "Thickness"
        '
        'LabelPS
        '
        Me.LabelPS.AutoSize = True
        Me.LabelPS.Location = New System.Drawing.Point(12, 212)
        Me.LabelPS.Name = "LabelPS"
        Me.LabelPS.Size = New System.Drawing.Size(55, 13)
        Me.LabelPS.TabIndex = 14
        Me.LabelPS.Text = "Plane size"
        '
        'LabelPD
        '
        Me.LabelPD.AutoSize = True
        Me.LabelPD.Location = New System.Drawing.Point(12, 238)
        Me.LabelPD.Name = "LabelPD"
        Me.LabelPD.Size = New System.Drawing.Size(77, 13)
        Me.LabelPD.TabIndex = 15
        Me.LabelPD.Text = "Plane distance"
        '
        'Form1
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(153, 299)
        Me.Controls.Add(Me.LabelSnapTol)
        Me.Controls.Add(Me.NumSnapTol)
        Me.Controls.Add(Me.ButtFree)
        Me.Controls.Add(Me.LabelPD)
        Me.Controls.Add(Me.NumPD)
        Me.Controls.Add(Me.LabelPS)
        Me.Controls.Add(Me.NumPS)
        Me.Controls.Add(Me.LabelThk)
        Me.Controls.Add(Me.NumThk)
        Me.Controls.Add(Me.LabelArrow)
        Me.Controls.Add(Me.NumAH)
        Me.Controls.Add(Me.LabelRad)
        Me.Controls.Add(Me.ButtScale)
        Me.Controls.Add(Me.ButtRotate)
        Me.Controls.Add(Me.ButtPlane)
        Me.Controls.Add(Me.ButtTranslate)
        Me.Controls.Add(Me.NumRad)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle
        Me.MaximizeBox = False
        Me.MinimizeBox = False
        Me.Name = "Form1"
        Me.ControlBox = True
        Me.KeyPreview = True
        Me.ShowIcon = False
        Me.ShowInTaskbar = False
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Gumball Attributes"
        CType(Me.NumRad, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumAH, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumThk, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumPS, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumPD, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumSnapTol, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents NumRad As NumericUpDown
    Friend WithEvents ButtTranslate As CheckBox
    Friend WithEvents ButtPlane As CheckBox
    Friend WithEvents ButtRotate As CheckBox
    Friend WithEvents ButtScale As CheckBox
    Friend WithEvents LabelRad As Label
    Friend WithEvents NumAH As NumericUpDown
    Friend WithEvents LabelArrow As Label
    Friend WithEvents LabelThk As Label
    Friend WithEvents NumThk As NumericUpDown
    Friend WithEvents LabelPS As Label
    Friend WithEvents NumPS As NumericUpDown
    Friend WithEvents LabelPD As Label
    Friend WithEvents NumPD As NumericUpDown
    Friend WithEvents ButtFree As CheckBox
    Friend WithEvents NumSnapTol As NumericUpDown
    Friend WithEvents LabelSnapTol As Label
#End Region

End Class

Public Class FormTextBox
    Inherits System.Windows.Forms.Form

    Private Shared _activeInstance As FormTextBox

    ''' <summary>Second Rhino MouseCallback route: fires for viewport presses even while the numeric WinForms window has focus.</summary>
    Friend Shared Function ConsumeBackdropMouseDown() As Boolean
        Dim f As FormTextBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed OrElse Not f.Visible Then Return False
        If f._committing OrElse Not f._outsideDismissReady Then Return False
        If Environment.TickCount < f._suppressBackdropDismissUntil Then Return False
        f.TryDismissFromOutsideRhinoGesture()
        Return True
    End Function

    Friend Shared Sub RequestDismissFromBackdropMouse()
        ConsumeBackdropMouseDown()
    End Sub

    Public GB As GhGumball
    Private _committing As Boolean
    Private _outsideDismissReady As Boolean
    ''' <summary>Rhino and GH handlers ignore dismiss briefly so the opening click-release does not close the fresh float.</summary>
    Private _suppressBackdropDismissUntil As Integer
    Private _hookedCanvas As GH_Canvas

    Sub New(screenLocation As Drawing.Point, MyOwner As GhGumball)
        ClosePriorFloatingLeakNoCancelPending()
        GB = MyOwner
        InitializeComponent()
        Me.StartPosition = FormStartPosition.Manual
        Dim padX As Integer = 10
        Dim padY As Integer = -24
        Dim loc As New Drawing.Point(screenLocation.X + padX, screenLocation.Y + padY)
        Dim wa As Drawing.Rectangle = System.Windows.Forms.Screen.GetWorkingArea(loc)
        If (loc.X < wa.Left) Then loc.X = wa.Left
        If (loc.Y < wa.Top) Then loc.Y = wa.Top
        If (loc.X + Me.Width > wa.Right) Then loc.X = Math.Max(wa.Left, wa.Right - Me.Width)
        If (loc.Y + Me.Height > wa.Bottom) Then loc.Y = Math.Max(wa.Top, wa.Bottom - Me.Height)
        Me.Location = loc
        _suppressBackdropDismissUntil = Environment.TickCount + 420
        _activeInstance = Me
        GumballNumericBackdropMouse.Instance.EnsureEnabled()
        Me.Show()
    End Sub

    Private Shared Sub ClosePriorFloatingLeakNoCancelPending()
        If (_activeInstance Is Nothing OrElse _activeInstance.IsDisposed) Then Return
        Dim p As FormTextBox = _activeInstance
        _activeInstance = Nothing
        p.SilentCloseLeakWithoutCancelPending()
    End Sub

    ''' <summary>Orphan/stacked floats: tear down the window without clearing gumball numeric pick state.</summary>
    Private Sub SilentCloseLeakWithoutCancelPending()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If GB IsNot Nothing Then GB.ForgetFloatingTextBox()
        GB = Nothing
        Try
            Close()
        Catch
        End Try
    End Sub

    Private Sub TryDismissFromOutsideRhinoGesture()
        If _committing OrElse Not _outsideDismissReady Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        If Not Visible Then Return
        Dim self As FormTextBox = Me
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
        If (_hookedCanvas Is Nothing) Then Return
        RemoveHandler _hookedCanvas.MouseDown, AddressOf Canvas_MouseDownDismissHook
        _hookedCanvas = Nothing
    End Sub

    Private Shared Sub RefreshBackdropMouseCallbackListening()
        If (_activeInstance Is Nothing OrElse _activeInstance.IsDisposed) Then
            GumballNumericBackdropMouse.Instance.Enabled = False
        Else
            GumballNumericBackdropMouse.Instance.Enabled = True
        End If
    End Sub

    ''' <summary>Cancel numeric edit (Escape, click outside, lost activation).</summary>
    Friend Sub DismissWithoutCommit()
        If (_committing) Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If (GB IsNot Nothing) Then GB.CancelPendingNumericInput()
        Close()
    End Sub

    Private Sub FormTextBox_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
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
        If (Not _outsideDismissReady OrElse _committing) Then Return
        BeginInvoke(Sub()
                        If (_committing) Then Return
                        If (Not Me.Visible) Then Return
                        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
                        DismissWithoutCommit()
                    End Sub)
    End Sub

    Private Sub FormTextBox_Deactivate(sender As Object, e As EventArgs) Handles MyBase.Deactivate
        If (Not _outsideDismissReady OrElse _committing) Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        DismissWithoutCommit()
    End Sub

    Private Sub TryCommitEntry()
        If (_committing OrElse GB Is Nothing OrElse TextBox1 Is Nothing) Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        Try
            GB.ValueString = TextBox1.Text.Trim()
            GB.TransformFromTextBox()
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
            e.SuppressKeyPress = True
            TryCommitEntry()
        End If
    End Sub

    Private Sub TextBox1_KeyPress(sender As Object, e As KeyPressEventArgs) Handles TextBox1.KeyPress
        If (e.KeyChar = ChrW(13) OrElse e.KeyChar = ChrW(10)) Then
            e.Handled = True
            TryCommitEntry()
        End If
    End Sub

    Private Sub FormTextBox_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        DetachGrasshopperCanvasDismissHookInternal()
    End Sub

    Private Sub FormTextBox_FormClosed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If ReferenceEquals(_activeInstance, Me) Then
            _activeInstance = Nothing
        End If
        RefreshBackdropMouseCallbackListening()
        If GB IsNot Nothing Then GB.DetachTextBoxForm()
    End Sub

    Private Sub TextBox1_TextChanged(sender As Object, e As EventArgs) Handles TextBox1.TextChanged
        If GB IsNot Nothing Then GB.ValueString = Me.TextBox1.Text
    End Sub

    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
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
        Me.TextBox1.Size = New System.Drawing.Size(100, 20)
        Me.TextBox1.TabIndex = 0
        Me.TextBox1.Multiline = False
        Me.TextBox1.AcceptsReturn = False
        '
        'Form1
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(100, 20)
        Me.Controls.Add(Me.TextBox1)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
        Me.StartPosition = FormStartPosition.Manual
        Me.MaximumSize = New System.Drawing.Size(100, 20)
        Me.MinimumSize = New System.Drawing.Size(100, 20)
        Me.Name = "Form1"
        Me.Text = "Form1"
        Me.Owner = Grasshopper.Instances.DocumentEditor
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents TextBox1 As TextBox
End Class