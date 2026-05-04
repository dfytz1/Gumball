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
        FormTextBox.RequestDismissFromBackdropMouse()
        FormAttributes.RequestDismissFromBackdropMouse()
    End Sub
End Class

Public Class GumballComp
    Inherits GH_Component

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

    Public Overrides Sub RemovedFromDocument(document As GH_Document)
        If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
    End Sub

    Public Overrides Sub MovedBetweenDocuments(oldDocument As GH_Document, newDocument As GH_Document)
        If (MyGumball IsNot Nothing) Then MyGumball.HideGumballs()
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
        If (MyGumball IsNot Nothing) Then
            If (Me.Hidden) Then MyGumball.HideGumballs()
            If Not (Me.Attributes.Selected) Then MyGumball.HideGumballs()
        End If
    End Sub

    Protected Overrides Sub AppendAdditionalComponentMenuItems(ByVal menu As Windows.Forms.ToolStripDropDown)

        Dim union As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Apply to all", AddressOf Me.Menu_ApplyToAll, True, Me.ModeValue(0) = 1)
        union.ToolTipText = "Performs transformation of a gumball to all geometry"

        Dim aling As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align to geometry", AddressOf Me.Menu_AlingToGeometry, True, CBool(Me.ModeValue(2)))
        aling.ToolTipText = "Use a geometry to align gumballs"

        Dim snapGeom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Snap to geometry", AddressOf Me.Menu_SnapToGeometry, True, CBool(Me.ModeValue(3)))
        snapGeom.ToolTipText = "Shows inputs S (targets) and t (max snap distance, doc units, optional): while translating grips, snaps toward the nearest point within that distance."

        Dim reloc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Relocate gumball", AddressOf Me.Menu_RelocateG, True, Me.ModeValue(0) = 2)
        reloc.ToolTipText = "Relocate gumball without affecting the geometry"

        Dim reset As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Reset gumball", AddressOf Me.Menu_Reset, True)
        reset.ToolTipText = "Restore gumball to world coordinates"

        Dim CC As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Me.Menu_ClearCache, True)
        CC.ToolTipText = "Reset gumball and clear cache data"

        Menu_AppendSeparator(menu)

        Dim preserveOnChange As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Preserve on changes", AddressOf Me.Menu_PreserveOnChanges, True, Me.PreserveTransformsOnGeometryChange)
        preserveOnChange.ToolTipText = "Keep gumball transforms when upstream geometry or the data tree changes (per item index when types match)."

        Dim proximityItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Me.Menu_ProximityCache, True, Me.ProximityCache)
        proximityItem.ToolTipText = "Like preserve on changes, but match each item to the prior gumball by nearest cached bounding‑box centre (same object type), so list shifts keep the right transform."

        Dim liveItem As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Live", AddressOf Me.Menu_LiveTransformsWhileDragging, True, Me.LiveTransformsWhileDragging)
        liveItem.ToolTipText = "Refresh downstream Grasshopper while dragging the gumball; one undo compound entry per finished drag."

        Menu_AppendSeparator(menu)

        Dim arrows As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Only arrows", AddressOf Me.Menu_OnlyArrows, True, Me.ModeValue(1) = 1)
        arrows.ToolTipText = "Show only arrows"

        Dim free As Windows.Forms.ToolStripItem = Menu_AppendItem(menu, "Free translate", AddressOf Me.Menu_FreeTranslate, True, Me.ModeValue(1) = 2)
        free.ToolTipText = "Hide all and drag from gumball center"

        Menu_AppendSeparator(menu)

        Dim Rad As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Attributes", AddressOf Me.Menu_Attributes, True)
        Rad.ToolTipText = "Changes the gumball attributes"

    End Sub

#End Region

#Region "Menu"
    Private Sub Menu_ApplyToAll()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        If (1 = Me.ModeValue(0)) Then
            Me.ModeValue(0) = 0
        Else
            Me.ModeValue(0) = 1
        End If
    End Sub

    Private Sub Menu_AlingToGeometry()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        Me.ModeValue(2) = CInt(Not CBool(Me.ModeValue(2)))
    End Sub

    Private Sub Menu_SnapToGeometry()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
        Me.ModeValue(3) = CInt(Not CBool(Me.ModeValue(3)))
    End Sub

    Private Sub Menu_RelocateG()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Gumball Mode", New GbUndo(Me.MyGumball))
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

    Private Sub Menu_OnlyArrows()
        If (MyGumball Is Nothing) Then Exit Sub
        Me.RecordUndoEvent("Gumball Attributes", New GbUndo(Me.MyGumball))
        If (ModeValueAtt = 1) Then
            ModeValueAtt = 0
            MyGumball.CustomAppearance = New Integer(9) {1, 1, 2, 1, 1, MyGumball.CustomAppearance(5), MyGumball.CustomAppearance(6),
                MyGumball.CustomAppearance(7), MyGumball.CustomAppearance(8), MyGumball.CustomAppearance(9)}
        Else
            ModeValueAtt = 1
            MyGumball.CustomAppearance = New Integer(9) {1, 0, 2, 0, 0, MyGumball.CustomAppearance(5), MyGumball.CustomAppearance(6),
                MyGumball.CustomAppearance(7), MyGumball.CustomAppearance(8), MyGumball.CustomAppearance(9)}
        End If

    End Sub

    Private Sub Menu_FreeTranslate()
        If (MyGumball Is Nothing) Then Exit Sub
        Me.RecordUndoEvent("Gumball Attributes", New GbUndo(Me.MyGumball))

        If (ModeValueAtt = 2) Then
            ModeValueAtt = 0
            MyGumball.CustomAppearance = New Integer(9) {1, 1, 2, 1, 1, MyGumball.CustomAppearance(5), MyGumball.CustomAppearance(6),
                 MyGumball.CustomAppearance(7), MyGumball.CustomAppearance(8), MyGumball.CustomAppearance(9)}
        Else
            ModeValueAtt = 2
            MyGumball.CustomAppearance = New Integer(9) {0, 0, 2, 0, 0, MyGumball.CustomAppearance(5), MyGumball.CustomAppearance(6),
                MyGumball.CustomAppearance(7), MyGumball.CustomAppearance(8), MyGumball.CustomAppearance(9)}
        End If

    End Sub

    Private Sub Menu_Reset()
        Me.RecordUndoEvent("Gumball Reset")
        If (MyGumball IsNot Nothing) Then MyGumball.RestoreGumball()
    End Sub

    Public Sub Menu_ClearCache()
        Me.RecordUndoEvent("Gumball Clear Cache")
        Me.Cache.Clear()
        If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
        Me.MyGumball = Nothing
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ProximityCache()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Proximity cache", New GbUndo(Me.MyGumball))
        Me.ProximityCache = Not Me.ProximityCache
    End Sub

    Private Sub Menu_PreserveOnChanges()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Preserve on changes", New GbUndo(Me.MyGumball))
        Me.PreserveTransformsOnGeometryChange = Not Me.PreserveTransformsOnGeometryChange
    End Sub

    Private Sub Menu_LiveTransformsWhileDragging()
        If (MyGumball IsNot Nothing) Then Me.RecordUndoEvent("Live (while dragging)", New GbUndo(Me.MyGumball))
        Me.LiveTransformsWhileDragging = Not Me.LiveTransformsWhileDragging
    End Sub
#End Region

    Private Function FindInputIndexByNickName(nick As String) As Integer
        For i As Integer = 0 To Params.Input.Count - 1
            If String.Equals(Params.Input(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Sub SyncSecondaryInputs()
        Dim changed As Boolean
        Do
            changed = False
            For i As Integer = Params.Input.Count - 1 To 1 Step -1
                Dim p As IGH_Param = Params.Input(i)
                Dim nick As String = p.NickName
                If String.Equals(nick, "A", StringComparison.OrdinalIgnoreCase) AndAlso Not ModeValueAlign Then
                    If MyGumball IsNot Nothing Then
                        MyGumball.GeometrytoAlign = Nothing
                        MyGumball.ClearAlignAxisReference()
                    End If
                    p.RemoveAllSources()
                    Params.UnregisterInputParameter(p)
                    changed = True
                    Exit For
                End If
                If String.Equals(nick, "t", StringComparison.OrdinalIgnoreCase) AndAlso Not ModeValueSnap Then
                    p.RemoveAllSources()
                    Params.UnregisterInputParameter(p)
                    changed = True
                    Exit For
                End If
                If String.Equals(nick, "S", StringComparison.OrdinalIgnoreCase) AndAlso Not ModeValueSnap Then
                    If MyGumball IsNot Nothing Then MyGumball.DisposeSnapTranslateTargets()
                    p.RemoveAllSources()
                    Params.UnregisterInputParameter(p)
                    changed = True
                    Exit For
                End If
            Next
        Loop While changed

        Dim hasA As Boolean = FindInputIndexByNickName("A") >= 0
        Dim hasS As Boolean = FindInputIndexByNickName("S") >= 0

        If ModeValueAlign AndAlso Not hasA Then
            Dim pa As New Grasshopper.Kernel.Parameters.Param_Geometry With {
                .Optional = True,
                .Name = "Geometry to align",
                .NickName = "A",
                .Description = "Plane (exact X/Y/Z axis reference) or other geometry to orient gumball axes.",
                .Access = GH_ParamAccess.item
            }
            Dim insertA As Integer = If(hasS, FindInputIndexByNickName("S"), Params.Input.Count)
            If insertA < 0 Then insertA = 1
            Params.RegisterInputParam(pa, insertA)
            Params.OnParametersChanged()
        End If

        hasS = FindInputIndexByNickName("S") >= 0
        If ModeValueSnap AndAlso Not hasS Then
            Dim ps As New Grasshopper.Kernel.Parameters.Param_Geometry With {
                .Optional = True,
                .Name = "Snap target",
                .NickName = "S",
                .Description = "Geometry to snap to while translating gumball grips.",
                .Access = GH_ParamAccess.tree
            }
            Params.RegisterInputParam(ps, Params.Input.Count)
            Params.OnParametersChanged()
        End If

        Dim hasT As Boolean = FindInputIndexByNickName("t") >= 0
        If ModeValueSnap AndAlso Not hasT Then
            Dim pTol As New Grasshopper.Kernel.Parameters.Param_Number With {
                .Optional = True,
                .Name = "Snap tolerance",
                .NickName = "t",
                .Description = "Maximum snap distance (model units). Leave empty for automatic (~220× document tolerance, minimum 0.02).",
                .Access = GH_ParamAccess.item
            }
            Params.RegisterInputParam(pTol, Params.Input.Count)
            Params.OnParametersChanged()
        End If

        Me.ExpireSolution(True)
    End Sub

    ''' <summary>Restore optional A/S inputs from GH file without triggering <see cref="ModeValue"/> setter side effects twice.</summary>
    Friend Sub ApplyOptionalInputModesFromFile(alignGeometry As Boolean, snapToGeometry As Boolean)
        ModeValueAlign = alignGeometry
        ModeValueSnap = snapToGeometry
        SyncSecondaryInputs()
    End Sub

    Private Sub RefreshSnapTranslateTargets(DA As IGH_DataAccess)
        If MyGumball Is Nothing Then Return
        MyGumball.DisposeSnapTranslateTargets()
        MyGumball.SnapTranslateRadiusOverride = Double.NaN
        If Not ModeValueSnap Then Return

        Dim tIx As Integer = FindInputIndexByNickName("t")
        If tIx >= 0 AndAlso Params.Input(tIx).VolatileDataCount > 0 Then
            Dim tolNum As Grasshopper.Kernel.Types.GH_Number = Nothing
            If DA.GetData(tIx, tolNum) AndAlso tolNum IsNot Nothing Then
                Try
                    If tolNum.IsValid Then
                        Dim tv As Double = tolNum.Value
                        If tv > 0 Then MyGumball.SnapTranslateRadiusOverride = tv
                    End If
                Catch
                End Try
            End If
        End If

        Dim ix As Integer = FindInputIndexByNickName("S")
        If ix < 0 OrElse Params.Input(ix).VolatileDataCount = 0 Then Return
        Dim data As New GH_Structure(Of Types.IGH_GeometricGoo)
        If Not DA.GetDataTree(ix, data) Then Return
        Dim list As New List(Of GeometryBase)
        For Each b As GH_Path In data.Paths
            For Each d As Types.IGH_GeometricGoo In data.Branch(b)
                If d Is Nothing Then Continue For
                Dim gb As GeometryBase = GH_Convert.ToGeometryBase(d)
                If gb IsNot Nothing Then list.Add(gb.Duplicate())
            Next
        Next
        If list.Count > 0 Then MyGumball.SnapTranslateTargets = list
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
                    ModeValueAtt = value
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
    Private Cache As New DataTree(Of GeometryBase)
    Private Paths As GH_Path()
    ''' <summary>Parallel to flattened input leaves: index into MyGumball geometry/Xform (-1 means null input at that leaf).</summary>
    Private _leafToGumballSlot As Integer()
    Public AttForm As FormAttributes = Nothing
    Private MyGumballAttributes As Integer() = New Integer(9) {1, 1, 2, 1, 1, 50, 5, 2, 15, 35}

    Private ModeValueType As New Integer
    Private ModeValueAlign As New Boolean
    ''' <summary>Right-click Snap to geometry: optional input S feeds translator snap targets.</summary>
    Private ModeValueSnap As Boolean
    Private ModeValueAtt As New Integer

    ''' <summary>When true, upstream geometry updates try to keep existing gumball transform stacks per non-null index instead of rebuilding from scratch.</summary>
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
    ''' When upstream changes, remap transforms onto new items by greedy nearest centroid (from cached upstream geometry).
    ''' Precedence over <see cref="PreserveTransformsOnGeometryChange"/> when resolving.
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

    Private Sub ApplyAlignGeometryInput(DA As IGH_DataAccess)
        Dim apIx As Integer = FindInputIndexByNickName("A")
        If apIx < 0 OrElse MyGumball Is Nothing OrElse Not ModeValueAlign Then Return
        If Params.Input(apIx).VolatileDataCount = 0 Then
            MyGumball.ClearAlignAxisReference()
            MyGumball.GeometrytoAlign = Nothing
            Return
        End If
        Dim gg As Types.IGH_GeometricGoo = Nothing
        If Not DA.GetData(apIx, gg) OrElse gg Is Nothing Then Return

        ' Re-running axis/geometry alignment resets GumballDisplayConduit bases and destroys in-progress GumballTransform.
        ' During Live + grip drag, SolveInstance would do that every Expire — preview stays on the widget only.
        If LiveTransformsWhileDragging AndAlso MyGumball.PreviewGripSlot >= 0 AndAlso Not (MyGumball.PreviewGripDelta = Transform.Identity) Then
            Return
        End If

        Dim axisPl As New Plane
        If TryUnpackAlignPlaneFromGoo(gg, axisPl) Then
            If Not MyGumball.HasAlignAxisReferencePlane OrElse Not AlignAxisFramesEqual(MyGumball.AlignAxisReferencePlane, axisPl) Then
                MyGumball.SetAlignToAxisReference(axisPl)
                If Attributes.Selected Then MyGumball.ShowGumballs()
            End If
            Return
        End If

        MyGumball.ClearAlignAxisReference()
        Dim gbAlign As GeometryBase = GH_Convert.ToGeometryBase(gg)
        If gbAlign Is Nothing Then Return
        Dim g As GeometryBase = gbAlign.Duplicate()
        If MyGumball.GeometrytoAlign Is Nothing Then
            MyGumball.AlignToGeometry(g)
        Else
            Dim gtree As New DataTree(Of GeometryBase)
            gtree.Add(g, New GH_Path(0))
            Dim atree As New DataTree(Of GeometryBase)
            atree.Add(MyGumball.GeometrytoAlign, New GH_Path(0))
            If Not AreEquals(gtree, atree) Then
                MyGumball.AlignToGeometry(g)
                If Attributes.Selected Then MyGumball.ShowGumballs()
            End If
        End If
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim Data As New GH_Structure(Of Types.IGH_GeometricGoo)
        Dim InputData As New DataTree(Of GeometryBase)

        'Get input data.
        If Not (DA.GetDataTree(0, Data)) Then Exit Sub

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
                    End If
                    leafIx += 1
                Next
            Next
        End If


        'Set cache.
        If (Cache.DataCount = 0) Then
            SetCache(InputData)
        Else
            'Test if new inputdata
            If Not (AreEquals(Cache, InputData)) Then
                Dim resynced As GhGumball = Nothing
                If ProximityCache AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                    resynced = GhGumball.CreateResyncPreservingTransformsProximity(nonNullGeom.ToArray(), Me, MyGumball, Cache)
                ElseIf PreserveTransformsOnGeometryChange AndAlso MyGumball IsNot Nothing AndAlso nonNullGeom.Count > 0 Then
                    resynced = GhGumball.CreateResyncPreservingTransforms(nonNullGeom.ToArray(), Me, MyGumball)
                End If
                Cache.Clear()
                If (MyGumball IsNot Nothing) Then MyGumball.Dispose()
                MyGumball = Nothing
                SetCache(InputData)
                If resynced IsNot Nothing Then
                    MyGumball = resynced
                    If Me.ModeValue(2) Then MyGumball.ReapplyStoredAlignment()
                    If (Me.Attributes.Selected) Then MyGumball.ShowGumballs()
                End If
            End If
        End If

        'Create Gumball class for non-null geometry only (no empty GhGumball array).
        If nonNullGeom.Count = 0 Then
            If (MyGumball IsNot Nothing) Then
                MyGumball.Dispose()
                MyGumball = Nothing
            End If
        ElseIf (MyGumball Is Nothing) Then
            MyGumball = New GhGumball(nonNullGeom.ToArray(), Me)
            If (Me.Attributes.Selected) Then MyGumball.ShowGumballs()
        End If

        ApplyAlignGeometryInput(DA)
        RefreshSnapTranslateTargets(DA)

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

                        Select Case ModeValue(0)

                            Case 1 ' Apply to all — preview transforms every slot equally.
                                Dim gx As GeometryBase = MyGumball.Geometry(slot).Duplicate()
                                gx.Transform(liveD)
                                d = GH_Convert.ToGeometricGoo(gx)
                                xfOut = ComposeGhTransformAppendGeneric(MyGumball.Xform(slot), liveD)

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
                        Dim CrvA As Curve = DirectCast(aCell, Curve)
                        Dim CrvB As Curve = DirectCast(bCell, Curve)
                        If (CrvA.ObjectType <> CrvB.ObjectType) Then
                            Return False
                            Exit Function
                        End If
                        If (CrvA.GetLength() <> CrvB.GetLength()) Then
                            Return False
                            Exit Function
                        End If
                        If (CrvA.Degree <> CrvB.Degree) Or (CrvA.Dimension <> CrvB.Dimension) Or (CrvA.Domain <> CrvB.Domain) Or
                            (CrvA.IsClosed <> CrvB.IsClosed) Or (CrvA.IsPeriodic <> CrvB.IsPeriodic) Or (CrvA.SpanCount <> CrvB.SpanCount) Then
                            Return False
                            Exit Function
                        End If
                        Dim paramA As Double() = CrvA.DivideByCount(40, True)
                        Dim paramB As Double() = CrvA.DivideByCount(40, True)
                        For j As Int32 = 0 To paramA.Count - 1
                            If (paramA(j) <> paramB(j)) Then
                                Return False
                                Exit Function
                            End If
                        Next

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
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(ByVal reader As GH_IO.Serialization.GH_IReader) As Boolean

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

End Class

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

            If (MyOwner.MyGumball IsNot Nothing) Then

                If Not (MyOwner.Hidden) AndAlso Not (MyOwner.Locked) Then

                    If (MyBase.Selected) Then
                        If Not (value) Then
                            MyOwner.MyGumball.HideGumballs()
                        End If
                    Else
                        If (value) Then
                            MyOwner.MyGumball.ShowGumballs()
                        End If
                    End If

                Else
                    MyOwner.MyGumball.HideGumballs()
                End If
                If (MyOwner.Params.Input(0).VolatileData.DataCount = 0) Then MyOwner.MyGumball.HideGumballs()
            End If

            MyBase.Selected = value
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
    ''' <summary>Flatten duplicated geometry from optional input S — used by translator snapping during viewport drags.</summary>
    Friend SnapTranslateTargets As List(Of GeometryBase)
    ''' <summary>Positive: max snap distance from input t (model units); NaN: use automatic default from document tolerance.</summary>
    Friend SnapTranslateRadiusOverride As Double = Double.NaN

    Friend Sub DisposeSnapTranslateTargets()
        If SnapTranslateTargets Is Nothing Then Return
        For Each g As GeometryBase In SnapTranslateTargets
            Try
                g.Dispose()
            Catch
            End Try
        Next
        SnapTranslateTargets = Nothing
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
    ''' Like <see cref="CreateResyncPreservingTransforms"/>, but each new slot takes transforms from the prior gumball entry whose cached upstream bbox centre is nearest (same object type).
    ''' </summary>
    Friend Shared Function CreateResyncPreservingTransformsProximity(newGeoms As GeometryBase(), comp As GumballComp, oldGb As GhGumball, oldCache As DataTree(Of GeometryBase)) As GhGumball
        Dim oldCentres As New List(Of Point3d)
        Dim oldKinds As New List(Of Rhino.DocObjects.ObjectType)
        ExtractNonNullBoundingBoxMeta(oldCache, oldCentres, oldKinds)
        If oldCentres.Count <> oldGb.Count Then
            Return CreateResyncPreservingTransforms(newGeoms, comp, oldGb)
        End If
        Return CreateResyncPreservingTransformsWithMap(newGeoms, comp, oldGb, GreedyCentroidSlotMap(oldCentres, oldKinds, newGeoms))
    End Function

    Private Structure ProximityCand
        Public Dist As Double
        Public OldIx As Integer
        Public NewIx As Integer
    End Structure

    Private Shared Function BuildIndexSlotMap(newCount As Integer, oldCount As Integer) As Integer()
        Dim map(newCount - 1) As Integer
        For j As Integer = 0 To newCount - 1
            map(j) = If(j < oldCount, j, -1)
        Next
        Return map
    End Function

    Private Shared Sub ExtractNonNullBoundingBoxMeta(tree As DataTree(Of GeometryBase), centres As List(Of Point3d), kinds As List(Of Rhino.DocObjects.ObjectType))
        For bi As Integer = 0 To tree.BranchCount - 1
            For j As Integer = 0 To tree.Branch(bi).Count - 1
                Dim cell As GeometryBase = tree.Branch(bi)(j)
                If cell IsNot Nothing Then
                    centres.Add(cell.GetBoundingBox(True).Center)
                    kinds.Add(cell.ObjectType)
                End If
            Next
        Next
    End Sub

    Private Shared Function GreedyCentroidSlotMap(oldCentres As List(Of Point3d), oldKinds As List(Of Rhino.DocObjects.ObjectType), newGeoms As GeometryBase()) As Integer()
        Dim nNew As Integer = newGeoms.Length
        Dim nOld As Integer = oldCentres.Count
        Dim map As Integer() = Enumerable.Repeat(-1, nNew).ToArray()
        If nOld = 0 OrElse nNew = 0 Then Return map

        Dim cands As New List(Of ProximityCand)
        For io As Integer = 0 To nOld - 1
            For jn As Integer = 0 To nNew - 1
                If oldKinds(io) = newGeoms(jn).ObjectType Then
                    cands.Add(New ProximityCand With {
                        .Dist = oldCentres(io).DistanceTo(newGeoms(jn).GetBoundingBox(True).Center),
                        .OldIx = io,
                        .NewIx = jn
                    })
                End If
            Next
        Next
        cands.Sort(Function(a As ProximityCand, b As ProximityCand) a.Dist.CompareTo(b.Dist))

        Dim usedOld As New BitArray(nOld)
        For Each c As ProximityCand In cands
            If Not usedOld(c.OldIx) AndAlso map(c.NewIx) < 0 Then
                usedOld(c.OldIx) = True
                map(c.NewIx) = c.OldIx
            End If
        Next
        Return map
    End Function

    Friend Shared Function CreateResyncPreservingTransformsWithMap(newGeoms As GeometryBase(), comp As GumballComp, oldGb As GhGumball, newSlotToOldSlot As Integer()) As GhGumball
        Dim gb As New GhGumball(newGeoms, comp)
        Dim clonedAtt(9) As Integer
        Array.Copy(oldGb.CustomAppearance, clonedAtt, 10)
        gb.CustomAppearance = clonedAtt
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
        Next

        Return gb
    End Function

    Private Shared Sub ApplyCompoundGenericTransformsInOrder(geo As GeometryBase, xf As Types.GH_Transform)
        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In xf.CompoundTransforms
            Dim gen = TryCast(t, Grasshopper.Kernel.Types.Transforms.Generic)
            If gen Is Nothing Then Continue For
            geo.Transform(gen.Transform)
        Next
    End Sub

    Private Sub RebuildGumballObjectAndConduitAt(i As Integer)
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
            comp.ModeValue(1) = att1.GetInt32("attmode", 1)
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
        If Conduits Is Nothing Then Return
        For i As Int32 = 0 To Conduits.Length - 1
            Conduits(i).Enabled = True
        Next
        Me.Enabled = True
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Public Sub HideGumballs()
        If Conduits Is Nothing Then Return
        For i As Int32 = 0 To Conduits.Length - 1
            Conduits(i).Enabled = False
        Next
        Me.Enabled = False
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Public Sub Dispose()
        CloseNumericTextBoxIfAny()
        TearDownRhinoEscapeHandler()
        PreviewGripSlot = -1
        PreviewGripDelta = Transform.Identity
        DisposeSnapTranslateTargets()
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
            If HasAlignAxisReferencePlane AndAlso AlignAxisReferencePlane.IsValid Then
                AlignToAxisReferencePlane()
            ElseIf GeometrytoAlign IsNot Nothing Then
                AlignToGeometry(GeometrytoAlign)
            End If
        End If

        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()

    End Sub

    Public Sub UpdateGumball(ByVal i As Integer, xform As Transform)

        Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
        Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
        Dim pln As Plane = gbframe.Plane
        pln.Transform(xform)

        If (Rhino.ApplicationSettings.ModelAidSettings.GridSnap) Then
            If (Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateFree Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateX Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateY Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZ Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateXY Or Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateYZ Or
                    Conduits(Index).PickResult.Mode = Rhino.UI.Gumball.GumballMode.TranslateZX) Then

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
            If HasAlignAxisReferencePlane AndAlso AlignAxisReferencePlane.IsValid Then
                AlignToAxisReferencePlane()
            ElseIf GeometrytoAlign IsNot Nothing Then
                AlignToGeometry(GeometrytoAlign)
            End If
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
            If HasAlignAxisReferencePlane AndAlso AlignAxisReferencePlane.IsValid Then
                AlignToAxisReferencePlane()
            ElseIf GeometrytoAlign IsNot Nothing Then
                AlignToGeometry(GeometrytoAlign)
            End If
        End If

        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Public Sub AlignToGeometry(Geo As GeometryBase)

        HasAlignAxisReferencePlane = False
        GeometrytoAlign = Geo

        For i As Int32 = 0 To Count - 1

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
                Continue For
            End If

            baseFrame.Plane = gbframe.Plane
            baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
            Gumballs(i).Frame = baseFrame
            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
            Conduits(i).Enabled = True

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
        AlignToAxisReferencePlane()
    End Sub

    ''' <summary>Align all gumball axes to <see cref="AlignAxisReferencePlane"/>; each gumball keeps its current origin (centre).</summary>
    Public Sub AlignToAxisReferencePlane()
        If Not HasAlignAxisReferencePlane OrElse Not AlignAxisReferencePlane.IsValid Then Return
        For i As Int32 = 0 To Count - 1
            Dim gbframe As Rhino.UI.Gumball.GumballFrame = Conduits(i).Gumball.Frame
            Dim baseFrame As Rhino.UI.Gumball.GumballFrame = Gumballs(i).Frame
            Dim o As Point3d = gbframe.Plane.Origin
            gbframe.Plane = New Plane(o, AlignAxisReferencePlane.XAxis, AlignAxisReferencePlane.YAxis)
            baseFrame.Plane = gbframe.Plane
            baseFrame.ScaleGripDistance = gbframe.ScaleGripDistance
            Gumballs(i).Frame = baseFrame
            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
            Conduits(i).Enabled = True
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
    End Sub

    Friend Sub ReapplyStoredAlignment()
        If Component Is Nothing OrElse Not Component.ModeValue(2) Then Return
        If HasAlignAxisReferencePlane AndAlso AlignAxisReferencePlane.IsValid Then
            AlignToAxisReferencePlane()
        ElseIf GeometrytoAlign IsNot Nothing Then
            AlignToGeometry(GeometrytoAlign)
        End If
    End Sub

    ''' <summary>Commits the current conduit delta to Grasshopper geometry and gumball bases (picked slot <paramref name="gripIndex"/>).</summary>
    Private Sub CommitGripTransform(ByVal gripIndex As Integer, gbxform As Transform)
        If gripIndex < 0 OrElse gripIndex >= Count OrElse gbxform = Transform.Identity Then Return

        Select Case Component.ModeValue(0)

            Case 0 'Normal.
                Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                    Xform(gripIndex).CompoundTransforms.Add(t.Duplicate())
                Next
                Xform(gripIndex).ClearCaches()
                Geometry(gripIndex).Transform(gbxform)
                UpdateGumball(gripIndex)

            Case 1 'Apply to all.
                Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                For i As Int32 = 0 To Count - 1
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
    End Sub

    ''' <summary>Solves one constrained gumball update for viewport coordinates — updates rotate/translate/scale grip graphics (Rhino “active grip” look) relative to plane.</summary>
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
        ApplySnapTranslateIfTranslatingGrip(c.PickResult.Mode, dragPoint)
        Return c.UpdateGumball(dragPoint, wordline)
    End Function

    Private Sub ApplySnapTranslateIfTranslatingGrip(mode As Rhino.UI.Gumball.GumballMode, ByRef dragPoint As Point3d)
        If Not IsTranslateGumballMode(mode) Then Return
        If SnapTranslateTargets Is Nothing OrElse SnapTranslateTargets.Count = 0 Then Return
        If Component Is Nothing OrElse Not CBool(Component.ModeValue(3)) Then Return

        Dim radiusSq As Double
        If Not Double.IsNaN(SnapTranslateRadiusOverride) AndAlso SnapTranslateRadiusOverride > 0 Then
            radiusSq = SnapTranslateRadiusOverride * SnapTranslateRadiusOverride
        Else
            Dim r As Double = SnapTranslateProximityRadius()
            radiusSq = r * r
        End If
        Dim bestDistSq As Double = Double.PositiveInfinity
        Dim bestPt As Point3d = dragPoint

        For Each geom As GeometryBase In SnapTranslateTargets
            Dim candidate As Point3d
            If Not TryClosestPointOnSnapGeometry(geom, dragPoint, candidate) Then Continue For
            Dim ds As Double = dragPoint.DistanceToSquared(candidate)
            If ds < radiusSq AndAlso ds < bestDistSq Then
                bestDistSq = ds
                bestPt = candidate
            End If
        Next

        If bestDistSq <= radiusSq Then dragPoint = bestPt
    End Sub

    Private Shared Function SnapTranslateProximityRadius() As Double
        Dim tol As Double = 0.001
        Try
            tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
        Catch
            tol = 0.001
        End Try
        Return Math.Max(tol * 220R, 0.02R)
    End Function

    Private Shared Function TryClosestPointOnSnapGeometry(geom As GeometryBase, p As Point3d, ByRef q As Point3d) As Boolean
        If geom Is Nothing Then Return False
        Try
            If Not geom.IsValid Then Return False
        Catch
            Return False
        End Try

        Dim ext As Extrusion = TryCast(geom, Extrusion)
        If ext IsNot Nothing Then
            Dim tb As Brep = ext.ToBrep()
            Try
                Return TryClosestBrepSnapPoint(tb, p, q)
            Finally
                tb.Dispose()
            End Try
        End If

        Select Case geom.ObjectType
            Case Rhino.DocObjects.ObjectType.Brep
                Return TryClosestBrepSnapPoint(DirectCast(geom, Brep), p, q)

            Case Rhino.DocObjects.ObjectType.Mesh
                Dim mesh As Mesh = DirectCast(geom, Mesh)
                Dim mp As MeshPoint = mesh.ClosestMeshPoint(p, 0.0#)
                If mp Is Nothing Then Return False
                q = mesh.PointAt(mp)
                Return True

            Case Rhino.DocObjects.ObjectType.Curve
                Dim crv As Curve = DirectCast(geom, Curve)
                Dim t As Double
                If Not crv.ClosestPoint(p, t) Then Return False
                q = crv.PointAt(t)
                Return True

            Case Rhino.DocObjects.ObjectType.Point
                q = DirectCast(geom, Rhino.Geometry.Point).Location
                Return True

            Case Else
                Dim srf As Surface = TryCast(geom, Surface)
                If srf IsNot Nothing Then
                    Dim u, v As Double
                    If Not srf.ClosestPoint(p, u, v) Then Return False
                    q = srf.PointAt(u, v)
                    Return True
                End If
                Dim crv2 As Curve = TryCast(geom, Curve)
                If crv2 IsNot Nothing Then
                    Dim tt As Double
                    If Not crv2.ClosestPoint(p, tt) Then Return False
                    q = crv2.PointAt(tt)
                    Return True
                End If
                Return False
        End Select
    End Function

    Private Shared Function TryClosestBrepSnapPoint(brep As Brep, p As Point3d, ByRef q As Point3d) As Boolean
        Dim cpt As New Point3d
        Dim ci As ComponentIndex
        Dim nv As Vector3d
        If brep.ClosestPoint(p, cpt, ci, Nothing, Nothing, 0, nv) Then
            q = cpt
            Return True
        End If
        Return False
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

        Dim Pick As New Rhino.Input.Custom.PickContext
        Pick.View = e.View
        Pick.PickStyle = Rhino.Input.Custom.PickStyle.PointPick
        Pick.SetPickTransform(e.View.ActiveViewport.GetPickTransform(e.ViewportPoint))
        Dim pickline As Line = Nothing
        e.View.ActiveViewport.GetFrustumLine(CDbl(e.ViewportPoint.X), CDbl(e.ViewportPoint.Y), pickline)
        Pick.PickLine = pickline
        Pick.UpdateClippingPlanes()

        For i As Int32 = 0 To Count - 1
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

        If (Index = -1) Or (Index >= Count) Then Exit Sub

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

        ' Click without drag: prime rotate/scale visuals only — translate priming snaps the conduit to an off-axis CPlane point and corrupts numeric move origins.
        If Not _gripExceededDragThreshold Then
            _dragPreTransformCaptured = False
            SaveUndo = False
            PreviewGripSlot = -1
            PreviewGripDelta = Transform.Identity
            Try
                If e.View IsNot Nothing Then
                    Dim ixPrimed As Integer = Index
                    If ixPrimed >= 0 AndAlso ixPrimed < Count AndAlso Not IsTranslateGumballMode(Conduits(ixPrimed).PickResult.Mode) Then
                        If TryViewportUpdateGumball(ixPrimed, e.View, _gripDownViewport) Then
                            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
                        End If
                    Else
                        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
                    End If
                    If Component IsNot Nothing AndAlso Component.LiveTransformsWhileDragging Then
                        Component.ExpireSolution(True)
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
        Dim finalXform As Transform = Conduits(gripIdx).GumballTransform

        PreviewGripSlot = -1
        PreviewGripDelta = Transform.Identity

        If SaveUndo AndAlso (finalXform <> Transform.Identity) Then
            Component.RecordUndoEvent("Gumball Drag", New GbUndo(Me))
        End If
        SaveUndo = False

        If finalXform <> Transform.Identity Then
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

        If Component.LiveTransformsWhileDragging OrElse (finalXform <> Transform.Identity) Then
            Component.ExpireSolution(True)
        End If

        _gripExceededDragThreshold = False
        Index = -1
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
                    If (Component.ModeValue(0) = 1) Then 'Apply to all.
                        For i As Int32 = 0 To Count - 1
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
                    If (Component.ModeValue(0) = 1) Then 'Apply to all.
                        For i As Int32 = 0 To Count - 1
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
                    If (Component.ModeValue(0) = 1) Then 'Apply to all.
                        For i As Int32 = 0 To Count - 1
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

            If (gbxform <> Transform.Identity) Then

                Select Case Component.ModeValue(0)

                    Case 0 'Normal
                        Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                        For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                            Xform(Index).CompoundTransforms.Add(t.Duplicate())
                        Next
                        Xform(Index).ClearCaches()
                        Geometry(Index).Transform(gbxform)
                        UpdateGumballFromTextBox(Index, gbxform)

                    Case 1 'Apply to all.
                        Dim ghXform As New Grasshopper.Kernel.Types.GH_Transform(New Grasshopper.Kernel.Types.Transforms.Generic(gbxform))
                        For i As Int32 = 0 To Count - 1
                            For Each t As Grasshopper.Kernel.Types.Transforms.ITransform In ghXform.CompoundTransforms
                                Xform(i).CompoundTransforms.Add(t.Duplicate())
                            Next
                            Xform(i).ClearCaches()
                            Geometry(i).Transform(gbxform)
                            UpdateGumballFromTextBox(i, gbxform)
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

    Private Sub ChangeAppearances()

        For i As Int32 = 0 To Count - 1
            Conduits(i).Enabled = False
            Conduits(i).SetBaseGumball(Gumballs(i), Appearances(i))
            Conduits(i).Enabled = True
        Next
        Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
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
            att1.SetBoolean("aligntogeometry", 2, Me.Component.ModeValue(2))
            att1.SetBoolean("preservexf", 3, Me.Component.PreserveTransformsOnGeometryChange)
            att1.SetBoolean("proximitycache", 4, Me.Component.ProximityCache)
            att1.SetBoolean("livetransform", 5, Me.Component.LiveTransformsWhileDragging)
            att1.SetBoolean("snaptogeometry", 6, CBool(Me.Component.ModeValue(3)))

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
            Component.ModeValue(1) = att1.GetInt32("attmode", 1)
            Dim alignF2 As Boolean = att1.GetBoolean("aligntogeometry", 2)
            Dim snapF2 As Boolean = False
            If Not att1.TryGetBoolean("snaptogeometry", 6, snapF2) Then snapF2 = False
            Component.ApplyOptionalInputModesFromFile(alignF2, snapF2)
            Component.PreserveTransformsOnGeometryChange = att1.GetBoolean("preservexf", 3)
            Dim proxStored2 As Boolean
            If att1.TryGetBoolean("proximitycache", 4, proxStored2) Then
                Component.ProximityCache = proxStored2
            Else
                Component.ProximityCache = False
            End If
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
                If (Me.Component.Attributes.Selected) Then Me.ShowGumballs()

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
    Friend Shared Sub RequestDismissFromBackdropMouse()
        Dim f As FormAttributes = _activeInstance
        If f Is Nothing OrElse f.IsDisposed Then Return
        f.TryDismissFromOutsideRhinoGesture()
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
        CType(Me.NumRad, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumAH, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumThk, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumPS, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumPD, System.ComponentModel.ISupportInitialize).BeginInit()
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
        Me.ClientSize = New System.Drawing.Size(153, 273)
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
#End Region

End Class

Public Class FormTextBox
    Inherits System.Windows.Forms.Form

    Private Shared _activeInstance As FormTextBox

    ''' <summary>Second Rhino MouseCallback route: fires for viewport presses even while the numeric WinForms window has focus.</summary>
    Friend Shared Sub RequestDismissFromBackdropMouse()
        Dim f As FormTextBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed Then Return
        f.TryDismissFromOutsideRhinoGesture()
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