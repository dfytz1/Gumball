Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Linq
Imports System.Windows.Forms
Imports Grasshopper
Imports Grasshopper.Kernel
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

Public Class TextTagComp
    Inherits GH_Component

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
        pManager.AddGeometryParameter("Location", "P", "Point (text faces camera) or plane (text drawn in plane) to place the tag.", GH_ParamAccess.list)
        pManager.AddNumberParameter("Size", "S", "Text height in model units.", GH_ParamAccess.item, 1.0R)
        pManager.AddColourParameter("Colour", "C", "Dot and text colour.", GH_ParamAccess.item, Color.Black)
        pManager.Param(1).Optional = True
        pManager.Param(2).Optional = True
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddTextParameter("Text", "T", "Entered text per location (empty string until typed).", GH_ParamAccess.list)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New TextTagCompAtt(Me)
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

        Dim preserve As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Preserve changes", AddressOf Menu_PreserveChanges, True, Me.PreserveChanges)
        preserve.ToolTipText = "Keep entered text (per item index) when upstream points/planes move or change."

        Dim proximity As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, Me.ProximityCache)
        proximity.ToolTipText = "When the list changes, re-attach each text to the nearest cached location instead of the list index, so list shifts keep the right text on the right point."

        Dim cc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Erase all entered text."

        Menu_AppendSeparator(menu)

        Dim hLeft As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align left", AddressOf Menu_AlignLeft, True, Me.HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Left)
        hLeft.ToolTipText = "Anchor text to the left of the tag point."

        Dim hMiddle As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align middle", AddressOf Menu_AlignMiddle, True, Me.HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Center)
        hMiddle.ToolTipText = "Anchor text horizontally centered on the tag point."

        Dim hRight As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align right", AddressOf Menu_AlignRight, True, Me.HorizontalAlign = Rhino.DocObjects.TextHorizontalAlignment.Right)
        hRight.ToolTipText = "Anchor text to the right of the tag point."

        Menu_AppendSeparator(menu)

        Dim vTop As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align top", AddressOf Menu_AlignTop, True, Me.VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Top)
        vTop.ToolTipText = "Anchor text above the tag point."

        Dim vMiddle As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align middle", AddressOf Menu_AlignMiddleV, True, Me.VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Middle)
        vMiddle.ToolTipText = "Anchor text vertically centered on the tag point."

        Dim vBottom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Align bottom", AddressOf Menu_AlignBottom, True, Me.VerticalAlign = Rhino.DocObjects.TextVerticalAlignment.Bottom)
        vBottom.ToolTipText = "Anchor text below the tag point."

        Menu_AppendSeparator(menu)

        Dim justifyLines As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Justify multiline lines", AddressOf Menu_JustifyMultilineLines, True, Me.JustifyMultilineLines)
        justifyLines.ToolTipText = "When text has multiple lines, align each line within the block (shorter lines shift to the chosen horizontal side) instead of only moving the whole block relative to the dot."
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Text Tag Preserve", New TextTagUndo(Me))
        PreserveChanges = Not PreserveChanges
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Text Tag Proximity", New TextTagUndo(Me))
        ProximityCache = Not ProximityCache
    End Sub

    Public Sub Menu_ClearCache()
        RecordUndoEvent("Text Tag Clear Cache", New TextTagUndo(Me))
        Texts.Clear()
        CloseTagTextBoxIfAny()
        Me.ClearData()
        Me.ExpireSolution(True)
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
        JustifyMultilineLines = Not JustifyMultilineLines
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

#End Region

#Region "State"

    ''' <summary>Entered text per item index (persisted in the GH file).</summary>
    Friend Texts As New List(Of String)

    ''' <summary>Keep texts when upstream locations change (on by default).</summary>
    Public PreserveChanges As Boolean = True

    ''' <summary>When the list changes, re-attach texts to the nearest cached location instead of the list index. Takes precedence over PreserveChanges.</summary>
    Public ProximityCache As Boolean = False

    ''' <summary>Horizontal text anchor at the tag point (default: center).</summary>
    Public HorizontalAlign As Rhino.DocObjects.TextHorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center

    ''' <summary>Vertical text anchor at the tag point (default: middle).</summary>
    Public VerticalAlign As Rhino.DocObjects.TextVerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Middle

    ''' <summary>When true, multiline text aligns each line within the block (shorter lines shift to the horizontal alignment side).</summary>
    Public JustifyMultilineLines As Boolean = True

    ''' <summary>Anchors from the last solve (world locations plus optional planes).</summary>
    Friend Slots As New List(Of TextTagSlot)

    ''' <summary>Cached anchors used to detect upstream changes when PreserveChanges is off.</summary>
    Private CacheSlots As List(Of TextTagSlot) = Nothing

    Friend TextHeight As Double = 1.0R
    Friend TagColour As Color = Color.Black

    Friend TagMouse As TextTagMouse
    Friend TagTextBox As FormTextTagBox = Nothing
    ''' <summary>Slot index currently being edited in the floating text box (-1 = none).</summary>
    Friend EditIndex As Integer = -1

    Friend Sub SetTagTextsFromUndo(newTexts As List(Of String), newPreserve As Boolean, newProximity As Boolean,
                                   newHAlign As Rhino.DocObjects.TextHorizontalAlignment, newVAlign As Rhino.DocObjects.TextVerticalAlignment,
                                   newJustifyLines As Boolean)
        Texts = New List(Of String)(newTexts)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        HorizontalAlign = newHAlign
        VerticalAlign = newVAlign
        JustifyMultilineLines = newJustifyLines
        CloseTagTextBoxIfAny()
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

    ''' <summary>Viewport clicks are live only when the component is selected on canvas, unlocked, previewed and has anchors.</summary>
    Friend Sub SyncMouse()
        Dim want As Boolean =
            Me.Attributes IsNot Nothing AndAlso
            Me.Attributes.Selected AndAlso
            Not Me.Locked AndAlso
            Not Me.Hidden AndAlso
            Slots.Count > 0
        If TagMouse IsNot Nothing Then
            If TagMouse.Enabled <> want Then TagMouse.Enabled = want
        End If
        If Not want Then CloseTagTextBoxIfAny()
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

    ''' <summary>Greedy nearest-location matching: each cached text claims the closest new anchor, so list shifts and reorders keep text with its point.</summary>
    Private Shared Function RemapTextsByProximity(oldSlots As List(Of TextTagSlot), oldTexts As List(Of String), newSlots As List(Of TextTagSlot)) As List(Of String)
        Dim result As New List(Of String)(newSlots.Count)
        For i As Integer = 0 To newSlots.Count - 1
            result.Add(String.Empty)
        Next

        Dim pairs As New List(Of Tuple(Of Double, Integer, Integer))
        Dim nOld As Integer = Math.Min(oldSlots.Count, oldTexts.Count)
        For oi As Integer = 0 To nOld - 1
            If String.IsNullOrEmpty(oldTexts(oi)) Then Continue For
            For ni As Integer = 0 To newSlots.Count - 1
                pairs.Add(Tuple.Create(oldSlots(oi).Location.DistanceToSquared(newSlots(ni).Location), oi, ni))
            Next
        Next
        pairs.Sort(Function(x, y) x.Item1.CompareTo(y.Item1))

        Dim usedOld As New HashSet(Of Integer)
        Dim usedNew As New HashSet(Of Integer)
        For Each p As Tuple(Of Double, Integer, Integer) In pairs
            If usedOld.Contains(p.Item2) OrElse usedNew.Contains(p.Item3) Then Continue For
            result(p.Item3) = oldTexts(p.Item2)
            usedOld.Add(p.Item2)
            usedNew.Add(p.Item3)
        Next
        Return result
    End Function

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim goos As New List(Of IGH_GeometricGoo)
        If Not DA.GetDataList(0, goos) Then
            Slots.Clear()
            CacheSlots = Nothing
            SyncMouse()
            Exit Sub
        End If

        Dim size As Double = 1.0R
        DA.GetData(1, size)
        If size <= 0 OrElse Double.IsNaN(size) Then size = 1.0R
        TextHeight = size

        Dim col As Color = Color.Black
        DA.GetData(2, col)
        TagColour = col

        Dim newSlots As New List(Of TextTagSlot)
        For Each g As IGH_GeometricGoo In goos
            If g Is Nothing Then Continue For
            Dim slot As New TextTagSlot

            Dim ghPl As GH_Plane = TryCast(g, GH_Plane)
            If ghPl IsNot Nothing AndAlso ghPl.IsValid Then
                slot.Plane = ghPl.Value
                slot.Location = ghPl.Value.Origin
                slot.HasPlane = True
                newSlots.Add(slot)
                Continue For
            End If

            Dim pt As Point3d = Point3d.Unset
            If GH_Convert.ToPoint3d(g, pt, GH_Conversion.Both) AndAlso pt.IsValid Then
                slot.Location = pt
                slot.Plane = Plane.Unset
                slot.HasPlane = False
                newSlots.Add(slot)
                Continue For
            End If

            Dim pl As New Plane
            If GH_Convert.ToPlane(g, pl, GH_Conversion.Both) AndAlso pl.IsValid Then
                slot.Plane = pl
                slot.Location = pl.Origin
                slot.HasPlane = True
                newSlots.Add(slot)
                Continue For
            End If

            Me.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "A location could not be read as a point or plane and was skipped.")
        Next

        If CacheSlots Is Nothing Then
            CacheSlots = newSlots
        ElseIf Not SlotsEqual(CacheSlots, newSlots) Then
            If ProximityCache Then
                Texts = RemapTextsByProximity(CacheSlots, Texts, newSlots)
            ElseIf Not PreserveChanges Then
                Texts.Clear()
            End If
            CacheSlots = newSlots
        End If

        Slots = newSlots

        While Texts.Count < Slots.Count
            Texts.Add(String.Empty)
        End While
        ' With preserve on, keep surplus texts so a list that shrinks and grows back recovers its entries.
        If Not PreserveChanges AndAlso Texts.Count > Slots.Count Then
            Texts.RemoveRange(Slots.Count, Texts.Count - Slots.Count)
        End If

        If EditIndex >= Slots.Count Then CloseTagTextBoxIfAny()

        DA.SetDataList(0, Texts.Take(Slots.Count).ToList())
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

    Private Shared Function MeasureTextLine(line As String, basePlane As Plane, height As Double) As TextLineMetrics
        Dim result As New TextLineMetrics With {.Width = 0, .Height = height * 1.2R}
        If String.IsNullOrEmpty(line) Then Return result
        Using t As New Text3d(line, basePlane, height)
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

    Private Shared Function MeasureTextBlockExtents(txt As String, pl As Plane, height As Double,
                                                    ByRef minX As Double, ByRef maxX As Double,
                                                    ByRef minY As Double, ByRef maxY As Double) As Boolean
        minX = 0 : maxX = 0 : minY = 0 : maxY = 0
        Using t As New Text3d(txt, pl, height)
            t.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
            t.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
            Dim bb As BoundingBox = t.BoundingBox
            If Not bb.IsValid Then Return False
            BoundingBoxPlaneExtents(bb, pl, minX, maxX, minY, maxY)
            Return True
        End Using
    End Function

    Private Sub DrawTagTextBlock(display As Rhino.Display.DisplayPipeline, txt As String, pl As Plane, col As Color)
        Dim minX, maxX, minY, maxY As Double
        If Not MeasureTextBlockExtents(txt, pl, TextHeight, minX, maxX, minY, maxY) Then Return
        Dim drawPl As Plane = PlaneForBlockAnchor(pl, minX, maxX, minY, maxY, HorizontalAlign, VerticalAlign)
        Using t3 As New Text3d(txt, drawPl, TextHeight)
            t3.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
            t3.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
            display.Draw3dText(t3, col)
        End Using
    End Sub

    Private Sub DrawTagText(display As Rhino.Display.DisplayPipeline, txt As String, pl As Plane, col As Color)
        If String.IsNullOrEmpty(txt) Then Return

        If Not JustifyMultilineLines OrElse Not TextHasMultipleLines(txt) Then
            DrawTagTextBlock(display, txt, pl, col)
            Return
        End If

        Dim lines As String() = SplitTextLines(txt)
        If lines.Length = 0 Then Return

        Dim minX, maxX, minY, maxY As Double
        If Not MeasureTextBlockExtents(txt, pl, TextHeight, minX, maxX, minY, maxY) Then Return
        Dim anchorX As Double = BlockAnchorLocalX(minX, maxX, HorizontalAlign)
        Dim anchorY As Double = BlockAnchorLocalY(minY, maxY, VerticalAlign)
        Dim blockWidth As Double = Math.Max(0, maxX - minX)

        Dim widths(lines.Length - 1) As Double
        Dim lineStep As Double = TextHeight * 1.2R
        For i As Integer = 0 To lines.Length - 1
            Dim m As TextLineMetrics = MeasureTextLine(lines(i), pl, TextHeight)
            widths(i) = m.Width
            lineStep = Math.Max(lineStep, m.Height)
        Next

        For i As Integer = 0 To lines.Length - 1
            If String.IsNullOrEmpty(lines(i)) Then Continue For
            Dim lineX As Double = minX + HorizontalLineOffset(widths(i), blockWidth, HorizontalAlign)
            Dim lineY As Double = maxY - i * lineStep
            Dim lineOrigin As Point3d = pl.Origin + pl.XAxis * (lineX - anchorX) + pl.YAxis * (lineY - anchorY)
            Dim linePl As New Plane(lineOrigin, pl.XAxis, pl.YAxis)
            Using tLine As New Text3d(lines(i), linePl, TextHeight)
                tLine.HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Left
                tLine.VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Top
                display.Draw3dText(tLine, col)
            End Using
        Next
    End Sub

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        If Slots.Count = 0 Then Return

        Dim col As Color = TagColour
        If Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected Then
            col = args.WireColour_Selected
        End If

        For i As Integer = 0 To Slots.Count - 1
            Dim s As TextTagSlot = Slots(i)
            Dim txt As String = If(i < Texts.Count, Texts(i), String.Empty)

            If String.IsNullOrEmpty(txt) Then
                args.Display.DrawPoint(s.Location, PointStyle.RoundSimple, 5, col)
            Else
                Dim pl As Plane
                If s.HasPlane Then
                    pl = s.Plane
                Else
                    ' Camera-facing text for point input.
                    pl = New Plane(s.Location, args.Viewport.CameraX, args.Viewport.CameraY)
                End If
                DrawTagText(args.Display, txt, pl, col)
            End If
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim bb As BoundingBox = BoundingBox.Empty
            For Each s As TextTagSlot In Slots
                bb.Union(s.Location)
            Next
            If bb.IsValid Then bb.Inflate(TextHeight * 10.0R)
            Return bb
        End Get
    End Property

#End Region

#Region "Write/Read"

    Public Overrides Function Write(writer As GH_IO.Serialization.GH_IWriter) As Boolean
        writer.SetBoolean("TT_Preserve", PreserveChanges)
        writer.SetBoolean("TT_Proximity", ProximityCache)
        writer.SetInt32("TT_HAlign", CInt(HorizontalAlign))
        writer.SetInt32("TT_VAlign", CInt(VerticalAlign))
        writer.SetBoolean("TT_JustifyLines", JustifyMultilineLines)
        writer.SetInt32("TT_Count", Texts.Count)
        For i As Integer = 0 To Texts.Count - 1
            writer.SetString("TT_Text", i, If(Texts(i), String.Empty))
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

        Dim hAlign As Integer = CInt(Rhino.DocObjects.TextHorizontalAlignment.Center)
        If reader.TryGetInt32("TT_HAlign", hAlign) Then
            HorizontalAlign = CType(hAlign, Rhino.DocObjects.TextHorizontalAlignment)
        End If

        Dim vAlign As Integer = CInt(Rhino.DocObjects.TextVerticalAlignment.Middle)
        If reader.TryGetInt32("TT_VAlign", vAlign) Then
            VerticalAlign = CType(vAlign, Rhino.DocObjects.TextVerticalAlignment)
        End If

        Dim justifyLines As Boolean = True
        reader.TryGetBoolean("TT_JustifyLines", justifyLines)
        JustifyMultilineLines = justifyLines

        Texts.Clear()
        Dim n As Integer = 0
        If reader.TryGetInt32("TT_Count", n) Then
            For i As Integer = 0 To n - 1
                Dim s As String = String.Empty
                reader.TryGetString("TT_Text", i, s)
                Texts.Add(If(s, String.Empty))
            Next
        End If
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
    Private _preserve As Boolean
    Private _proximity As Boolean
    Private _hAlign As Rhino.DocObjects.TextHorizontalAlignment
    Private _vAlign As Rhino.DocObjects.TextVerticalAlignment
    Private _justifyLines As Boolean

    Sub New(owner As TextTagComp)
        _ownerId = owner.InstanceGuid
        _texts = New List(Of String)(owner.Texts)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _hAlign = owner.HorizontalAlign
        _vAlign = owner.VerticalAlign
        _justifyLines = owner.JustifyMultilineLines
    End Sub

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
        Dim curPreserve As Boolean = comp.PreserveChanges
        Dim curProximity As Boolean = comp.ProximityCache
        Dim curH As Rhino.DocObjects.TextHorizontalAlignment = comp.HorizontalAlign
        Dim curV As Rhino.DocObjects.TextVerticalAlignment = comp.VerticalAlign
        Dim curJustify As Boolean = comp.JustifyMultilineLines
        comp.SetTagTextsFromUndo(_texts, _preserve, _proximity, _hAlign, _vAlign, _justifyLines)
        _texts = curTexts
        _preserve = curPreserve
        _proximity = curProximity
        _hAlign = curH
        _vAlign = curV
        _justifyLines = curJustify
    End Sub

End Class

''' <summary>Viewport clicks on the tag dot/text (enabled only while the component is selected on canvas).</summary>
Public Class TextTagMouse
    Inherits Rhino.UI.MouseCallback

    Private ReadOnly Comp As TextTagComp

    Sub New(owner As TextTagComp)
        Comp = owner
    End Sub

    ''' <summary>Pixel pick radius around the anchor (dot or text insertion point).</summary>
    Private Const PickRadiusPx As Double = 14.0R
    ''' <summary>Beyond this many pixels the gesture counts as a drag (e.g. moving the underlying point), not a click.</summary>
    Private Const ClickSlopPx As Double = 4.0R

    Private _pendingHit As Integer = -1
    Private _downViewport As Drawing.Point

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

        Dim hit As Integer = -1
        Dim bestDist As Double = Double.PositiveInfinity
        For i As Integer = 0 To Comp.Slots.Count - 1
            Dim wpt As Point3d = Comp.Slots(i).Location
            If Not vp.IsVisible(wpt) Then Continue For
            Dim spt As Rhino.Geometry.Point2d = vp.WorldToClient(wpt)
            Dim dx As Double = spt.X - CDbl(e.ViewportPoint.X)
            Dim dy As Double = spt.Y - CDbl(e.ViewportPoint.Y)
            Dim d2 As Double = dx * dx + dy * dy
            If d2 <= PickRadiusPx * PickRadiusPx AndAlso d2 < bestDist Then
                bestDist = d2
                hit = i
            End If
        Next

        If hit < 0 Then Exit Sub

        ' Do not cancel the event: a drag must still reach Rhino so the underlying point can be moved.
        ' The text box opens on mouse-up only if the cursor stayed within the click slop.
        _pendingHit = hit
        _downViewport = e.ViewportPoint
    End Sub

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)
        If _pendingHit < 0 Then Exit Sub
        Dim dx As Double = CDbl(e.ViewportPoint.X) - CDbl(_downViewport.X)
        Dim dy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_downViewport.Y)
        If (dx * dx + dy * dy) > (ClickSlopPx * ClickSlopPx) Then _pendingHit = -1
    End Sub

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseUp(e)
        Dim hit As Integer = _pendingHit
        _pendingHit = -1
        If hit < 0 Then Exit Sub
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If hit >= Comp.Slots.Count Then Exit Sub

        Comp.EditIndex = hit
        Dim current As String = If(hit < Comp.Texts.Count, Comp.Texts(hit), String.Empty)
        Comp.TagTextBox = New FormTextTagBox(Control.MousePosition, Comp, hit, current)
        e.Cancel = True
    End Sub

End Class

''' <summary>Floating multiline text entry for a tag; Enter commits, Shift+Enter adds a line, Escape / click elsewhere dismisses.</summary>
Public Class FormTextTagBox
    Inherits System.Windows.Forms.Form

    Private Shared _activeInstance As FormTextTagBox

    ''' <summary>Rhino MouseCallback route: dismiss when the viewport is pressed while this float has focus.</summary>
    Friend Shared Sub RequestDismissFromBackdropMouse()
        Dim f As FormTextTagBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed Then Return
        f.TryDismissFromOutsideRhinoGesture()
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
