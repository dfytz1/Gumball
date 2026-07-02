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

Public Class CurveSliderComp
    Inherits GH_Component

    Public Sub New()
        MyBase.New("Curve Slider", "CrvSlider",
                   "Viewport slider on a curve: drag the dot along the curve (component selected), or click it to type a value. Right-click for normalized/real values, custom domain and cache options.",
                   "Params", "Util")
        SliderMouse = New CurveSliderMouse(Me)
    End Sub

#Region "Component overrides"

    Private Shared _icon As Bitmap

    Private Shared Function BuildIcon24x24() As Bitmap
        Const w As Integer = 24, h As Integer = 24
        Dim bmp As New Bitmap(w, h, PixelFormat.Format32bppArgb)
        Using g As Graphics = Graphics.FromImage(bmp)
            g.Clear(Color.Transparent)
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            Using pn As New Pen(Color.FromArgb(255, 40, 40, 40), 2)
                g.DrawBezier(pn, 2, 19, 8, 4, 16, 20, 22, 6)
            End Using
            Using br As New SolidBrush(Color.FromArgb(255, 230, 110, 55))
                g.FillEllipse(br, 9, 8, 8, 8)
            End Using
            Using pn As New Pen(Color.FromArgb(255, 40, 40, 40), 1)
                g.DrawEllipse(pn, 9, 8, 8, 8)
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
            Return New Guid("{e4a7c8d1-52f3-4b09-8c6e-9d1f2a3b4c5d}")
        End Get
    End Property

    Protected Overrides Sub RegisterInputParams(pManager As GH_Component.GH_InputParamManager)
        pManager.AddCurveParameter("Curve", "C", "Curve(s) to place a slider point on.", GH_ParamAccess.list)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddPointParameter("Point", "P", "Slider point on each curve.", GH_ParamAccess.list)
        pManager.AddNumberParameter("Value", "t", "Slider value per curve (normalized 0-1, real curve parameter, or custom-domain value per right-click settings).", GH_ParamAccess.list)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New CurveSliderCompAtt(Me)
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
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

#End Region

#Region "Menu"

    Protected Overrides Sub AppendAdditionalComponentMenuItems(menu As Windows.Forms.ToolStripDropDown)

        Dim realVals As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Real values", AddressOf Menu_RealValues, True, Me.ShowRealValue)
        realVals.ToolTipText = "Show and enter values in the curve's real parameter domain instead of normalized 0-1. Ignored while a custom domain is set."

        Dim customDom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Custom domain", AddressOf Menu_CustomDomain, True, Me.CustomDomain)
        customDom.ToolTipText = "Adds a D input: slider values are shown, entered and output in that domain (0-1 remapped)."

        Dim snapVals As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Snapping values", AddressOf Menu_SnapValues, True, Me.SnapValues)
        snapVals.ToolTipText = "Adds an S input (list of values in the current display units): dragging sticks to those values, shown as short ticks on the curve."

        Menu_AppendSeparator(menu)

        Dim preserve As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Preserve changes", AddressOf Menu_PreserveChanges, True, Me.PreserveChanges)
        preserve.ToolTipText = "Keep slider values (per item index) when upstream curves move or change."

        Dim proximity As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, Me.ProximityCache)
        proximity.ToolTipText = "When the curve list changes, re-attach each slider to the nearest new curve by point location instead of the list index."

        Dim cc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Reset all slider values to the curve midpoint."
    End Sub

    Private Sub Menu_RealValues()
        RecordUndoEvent("Curve Slider Values", New CurveSliderUndo(Me))
        ShowRealValue = Not ShowRealValue
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub Menu_CustomDomain()
        RecordUndoEvent("Curve Slider Domain", New CurveSliderUndo(Me))
        CustomDomain = Not CustomDomain
        SyncOptionalInputs()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_SnapValues()
        RecordUndoEvent("Curve Slider Snapping", New CurveSliderUndo(Me))
        SnapValues = Not SnapValues
        SyncOptionalInputs()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Curve Slider Preserve", New CurveSliderUndo(Me))
        PreserveChanges = Not PreserveChanges
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Curve Slider Proximity", New CurveSliderUndo(Me))
        ProximityCache = Not ProximityCache
    End Sub

    Public Sub Menu_ClearCache()
        RecordUndoEvent("Curve Slider Clear Cache", New CurveSliderUndo(Me))
        SliderParams.Clear()
        CacheCurves = Nothing
        CloseSliderTextBoxIfAny()
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

#End Region

#Region "Optional inputs"

    Private Function FindInputIndexByNick(nick As String) As Integer
        For i As Integer = 0 To Me.Params.Input.Count - 1
            If String.Equals(Me.Params.Input(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Function FindDomainInputIndex() As Integer
        Return FindInputIndexByNick("D")
    End Function

    Private Function FindSnapInputIndex() As Integer
        Return FindInputIndexByNick("S")
    End Function

    ''' <summary>Register/unregister the optional D (domain) and S (snap values) inputs to match the menu flags. Order: C, D, S.</summary>
    Friend Sub SyncOptionalInputs()
        Dim changed As Boolean = False

        Dim dIx As Integer = FindDomainInputIndex()
        If Not CustomDomain AndAlso dIx >= 0 Then
            Dim p As IGH_Param = Me.Params.Input(dIx)
            p.RemoveAllSources()
            Me.Params.UnregisterInputParameter(p)
            changed = True
        End If

        Dim sIx As Integer = FindSnapInputIndex()
        If Not SnapValues AndAlso sIx >= 0 Then
            Dim p As IGH_Param = Me.Params.Input(sIx)
            p.RemoveAllSources()
            Me.Params.UnregisterInputParameter(p)
            changed = True
        End If

        If CustomDomain AndAlso FindDomainInputIndex() < 0 Then
            Dim pd As New Grasshopper.Kernel.Parameters.Param_Interval With {
                .Optional = True,
                .Name = "Domain",
                .NickName = "D",
                .Description = "Custom value domain for the slider (values remapped from the curve's 0-1).",
                .Access = GH_ParamAccess.item
            }
            Dim insertAt As Integer = If(FindSnapInputIndex() >= 0, FindSnapInputIndex(), Me.Params.Input.Count)
            Me.Params.RegisterInputParam(pd, insertAt)
            changed = True
        End If

        If SnapValues AndAlso FindSnapInputIndex() < 0 Then
            Dim ps As New Grasshopper.Kernel.Parameters.Param_Number With {
                .Optional = True,
                .Name = "Snap values",
                .NickName = "S",
                .Description = "Values (in the current display units) the slider sticks to while dragging; drawn as short ticks on the curve.",
                .Access = GH_ParamAccess.list
            }
            Me.Params.RegisterInputParam(ps, Me.Params.Input.Count)
            changed = True
        End If

        If changed Then Me.Params.OnParametersChanged()
    End Sub

#End Region

#Region "State"

    ''' <summary>Normalized slider parameter (0-1) per curve index (persisted in the GH file).</summary>
    Friend SliderParams As New List(Of Double)

    Public PreserveChanges As Boolean = True
    Public ProximityCache As Boolean = False
    ''' <summary>Show/enter real curve-domain parameters instead of normalized 0-1.</summary>
    Public ShowRealValue As Boolean = True
    ''' <summary>Adds the D input; values are shown/entered/output remapped into that domain.</summary>
    Public CustomDomain As Boolean = False
    ''' <summary>Adds the S input; dragging sticks to those values, drawn as short ticks on the curve.</summary>
    Public SnapValues As Boolean = False

    ''' <summary>Curves from the last solve (duplicates).</summary>
    Friend Curves As New List(Of Curve)
    ''' <summary>Evaluated slider points from the last solve (parallel to Curves).</summary>
    Friend Points As New List(Of Point3d)

    ''' <summary>Cached curves used to detect upstream changes.</summary>
    Private CacheCurves As List(Of Curve) = Nothing

    ''' <summary>Custom domain from the D input this solve (Unset when absent/invalid).</summary>
    Friend CustomInterval As Interval = Interval.Unset

    ''' <summary>Snap values from the S input this solve (in display units).</summary>
    Friend SnapDisplayValues As New List(Of Double)

    Friend SliderMouse As CurveSliderMouse
    Friend SliderTextBox As FormCurveSliderBox = Nothing
    ''' <summary>Slot index currently being edited in the floating box (-1 = none).</summary>
    Friend EditIndex As Integer = -1

    Friend Function HasValidCustomInterval() As Boolean
        Return CustomDomain AndAlso CustomInterval.IsValid AndAlso Math.Abs(CustomInterval.Length) > Rhino.RhinoMath.ZeroTolerance
    End Function

    ''' <summary>Display value at a normalized curve parameter (for tick labels), per current settings.</summary>
    Friend Function DisplayValueAtNormalized(index As Integer, tNorm As Double) As Double
        Dim t As Double = Math.Max(0.0R, Math.Min(1.0R, tNorm))
        If HasValidCustomInterval() Then Return CustomInterval.ParameterAt(t)
        If ShowRealValue AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            Return Curves(index).Domain.ParameterAt(t)
        End If
        Return t
    End Function

    ''' <summary>Normalized t → displayed/output value per current settings.</summary>
    Friend Function DisplayValue(index As Integer) As Double
        Dim t As Double = If(index < SliderParams.Count, SliderParams(index), 0.5R)
        If HasValidCustomInterval() Then Return CustomInterval.ParameterAt(t)
        If ShowRealValue AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            Return Curves(index).Domain.ParameterAt(t)
        End If
        Return t
    End Function

    ''' <summary>Displayed value → normalized t without clamping; False when the mapping is undefined.</summary>
    Friend Function TryNormalizedFromDisplayValueUnclamped(index As Integer, value As Double, ByRef t As Double) As Boolean
        If HasValidCustomInterval() Then
            t = CustomInterval.NormalizedParameterAt(value)
        ElseIf ShowRealValue AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            t = Curves(index).Domain.NormalizedParameterAt(value)
        Else
            t = value
        End If
        Return Not Double.IsNaN(t) AndAlso Not Double.IsInfinity(t)
    End Function

    ''' <summary>Normalized snap parameters (0-1) for one curve, from the S input values in display units.</summary>
    Friend Function SnapParamsForCurve(index As Integer) As List(Of Double)
        Dim result As New List(Of Double)
        If Not SnapValues OrElse SnapDisplayValues.Count = 0 Then Return result
        For Each v As Double In SnapDisplayValues
            Dim t As Double
            If Not TryNormalizedFromDisplayValueUnclamped(index, v, t) Then Continue For
            If t < -0.000001R OrElse t > 1.000001R Then Continue For
            result.Add(Math.Max(0.0R, Math.Min(1.0R, t)))
        Next
        Return result
    End Function

    ''' <summary>Displayed value → normalized t per current settings (clamped 0-1).</summary>
    Friend Function NormalizedFromDisplayValue(index As Integer, value As Double) As Double
        Dim t As Double
        If HasValidCustomInterval() Then
            t = CustomInterval.NormalizedParameterAt(value)
        ElseIf ShowRealValue AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            t = Curves(index).Domain.NormalizedParameterAt(value)
        Else
            t = value
        End If
        If Double.IsNaN(t) Then t = 0.5R
        Return Math.Max(0.0R, Math.Min(1.0R, t))
    End Function

    Friend Sub SetStateFromUndo(newParams As List(Of Double), newPreserve As Boolean, newProximity As Boolean,
                                newReal As Boolean, newCustomDomain As Boolean, newSnapValues As Boolean)
        SliderParams = New List(Of Double)(newParams)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        ShowRealValue = newReal
        Dim needSync As Boolean = (CustomDomain <> newCustomDomain) OrElse (SnapValues <> newSnapValues)
        CustomDomain = newCustomDomain
        SnapValues = newSnapValues
        If needSync Then SyncOptionalInputs()
        CloseSliderTextBoxIfAny()
        Me.ExpireSolution(True)
    End Sub

    Friend Sub CloseSliderTextBoxIfAny()
        If SliderTextBox Is Nothing Then Return
        Dim tb As FormCurveSliderBox = SliderTextBox
        SliderTextBox = Nothing
        EditIndex = -1
        tb.DismissWithoutCommit()
    End Sub

    Friend Sub ForgetFloatingSliderTextBox()
        SliderTextBox = Nothing
    End Sub

    Friend Sub CancelPendingValueInput()
        EditIndex = -1
    End Sub

    ''' <summary>Commit a typed value from the floating box (interpreted per current display mode).</summary>
    Friend Sub CommitSliderValue(index As Integer, valueText As String)
        EditIndex = -1
        If index < 0 OrElse index >= Curves.Count Then Return
        Dim v As Double
        Try
            v = Convert.ToDouble(valueText.Trim())
        Catch
            Rhino.RhinoApp.WriteLine("Curve Slider: invalid value. Only numerical values are allowed.")
            Return
        End Try
        Dim t As Double = NormalizedFromDisplayValue(index, v)
        While SliderParams.Count < Curves.Count
            SliderParams.Add(0.5R)
        End While
        If Math.Abs(SliderParams(index) - t) < 0.0000000001R Then Return
        RecordUndoEvent("Curve Slider Value", New CurveSliderUndo(Me))
        SliderParams(index) = t
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    ''' <summary>During a viewport drag: set param and refresh (undo is registered once on mouse-up by the caller).</summary>
    Friend Sub DragSliderParam(index As Integer, t As Double)
        If index < 0 OrElse index >= Curves.Count Then Return
        While SliderParams.Count < Curves.Count
            SliderParams.Add(0.5R)
        End While
        SliderParams(index) = Math.Max(0.0R, Math.Min(1.0R, t))
        Me.ExpireSolution(True)
    End Sub

    Private Sub ShutDownInteraction()
        CloseSliderTextBoxIfAny()
        If SliderMouse IsNot Nothing Then SliderMouse.Enabled = False
    End Sub

    ''' <summary>Viewport interaction is live only when the component is selected on canvas, unlocked, previewed and has curves.</summary>
    Friend Sub SyncMouse()
        Dim want As Boolean =
            Me.Attributes IsNot Nothing AndAlso
            Me.Attributes.Selected AndAlso
            Not Me.Locked AndAlso
            Not Me.Hidden AndAlso
            Curves.Count > 0
        If SliderMouse IsNot Nothing Then
            If SliderMouse.Enabled <> want Then SliderMouse.Enabled = want
        End If
        If Not want Then CloseSliderTextBoxIfAny()
    End Sub

#End Region

#Region "Solve"

    Private Shared Function CurvesEqual(a As List(Of Curve), b As List(Of Curve)) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Count <> b.Count Then Return False
        Const tol As Double = 0.0001
        For i As Integer = 0 To a.Count - 1
            Dim ca As Curve = a(i)
            Dim cb As Curve = b(i)
            If ca Is Nothing OrElse cb Is Nothing Then
                If Not (ca Is Nothing AndAlso cb Is Nothing) Then Return False
                Continue For
            End If
            If ca.Degree <> cb.Degree Then Return False
            If ca.IsClosed <> cb.IsClosed Then Return False
            If ca.Domain <> cb.Domain Then Return False
            If Math.Abs(ca.GetLength() - cb.GetLength()) > tol Then Return False
            If ca.PointAtStart.DistanceTo(cb.PointAtStart) > tol Then Return False
            If ca.PointAtEnd.DistanceTo(cb.PointAtEnd) > tol Then Return False
            Dim midA As Point3d = ca.PointAt(ca.Domain.ParameterAt(0.5R))
            Dim midB As Point3d = cb.PointAt(cb.Domain.ParameterAt(0.5R))
            If midA.DistanceTo(midB) > tol Then Return False
        Next
        Return True
    End Function

    Private Shared Function PointOnCurveAtNormalized(crv As Curve, t As Double) As Point3d
        If crv Is Nothing Then Return Point3d.Unset
        Return crv.PointAt(crv.Domain.ParameterAt(Math.Max(0.0R, Math.Min(1.0R, t))))
    End Function

    ''' <summary>Greedy nearest matching: each old slider point claims the closest new curve; its param becomes the projection of the old point onto that curve.</summary>
    Private Shared Function RemapParamsByProximity(oldCurves As List(Of Curve), oldParams As List(Of Double), newCurves As List(Of Curve)) As List(Of Double)
        Dim result As New List(Of Double)(newCurves.Count)
        For i As Integer = 0 To newCurves.Count - 1
            result.Add(0.5R)
        Next

        Dim nOld As Integer = Math.Min(oldCurves.Count, oldParams.Count)
        Dim pairs As New List(Of Tuple(Of Double, Integer, Integer, Double))
        For oi As Integer = 0 To nOld - 1
            If oldCurves(oi) Is Nothing Then Continue For
            Dim oldPt As Point3d = PointOnCurveAtNormalized(oldCurves(oi), oldParams(oi))
            If Not oldPt.IsValid Then Continue For
            For ni As Integer = 0 To newCurves.Count - 1
                Dim nc As Curve = newCurves(ni)
                If nc Is Nothing Then Continue For
                Dim tRaw As Double
                If Not nc.ClosestPoint(oldPt, tRaw) Then Continue For
                Dim cp As Point3d = nc.PointAt(tRaw)
                Dim tNorm As Double = nc.Domain.NormalizedParameterAt(tRaw)
                pairs.Add(Tuple.Create(oldPt.DistanceToSquared(cp), oi, ni, tNorm))
            Next
        Next
        pairs.Sort(Function(x, y) x.Item1.CompareTo(y.Item1))

        Dim usedOld As New HashSet(Of Integer)
        Dim usedNew As New HashSet(Of Integer)
        For Each p As Tuple(Of Double, Integer, Integer, Double) In pairs
            If usedOld.Contains(p.Item2) OrElse usedNew.Contains(p.Item3) Then Continue For
            result(p.Item3) = Math.Max(0.0R, Math.Min(1.0R, p.Item4))
            usedOld.Add(p.Item2)
            usedNew.Add(p.Item3)
        Next
        Return result
    End Function

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim inCurves As New List(Of Curve)
        If Not DA.GetDataList(0, inCurves) Then
            Curves.Clear()
            Points.Clear()
            CacheCurves = Nothing
            SyncMouse()
            Exit Sub
        End If

        CustomInterval = Interval.Unset
        If CustomDomain Then
            Dim dIx As Integer = FindDomainInputIndex()
            If dIx >= 0 AndAlso Me.Params.Input(dIx).VolatileDataCount > 0 Then
                Dim iv As Interval = Interval.Unset
                If DA.GetData(dIx, iv) AndAlso iv.IsValid Then CustomInterval = iv
            End If
        End If

        SnapDisplayValues.Clear()
        If SnapValues Then
            Dim sIx As Integer = FindSnapInputIndex()
            If sIx >= 0 AndAlso Me.Params.Input(sIx).VolatileDataCount > 0 Then
                Dim vals As New List(Of Double)
                If DA.GetDataList(sIx, vals) Then
                    For Each v As Double In vals
                        If Not Double.IsNaN(v) AndAlso Not Double.IsInfinity(v) Then SnapDisplayValues.Add(v)
                    Next
                End If
            End If
        End If

        Dim newCurves As New List(Of Curve)
        For Each c As Curve In inCurves
            If c Is Nothing Then
                newCurves.Add(Nothing)
            Else
                newCurves.Add(c.DuplicateCurve())
            End If
        Next

        If CacheCurves Is Nothing Then
            CacheCurves = newCurves
        ElseIf Not CurvesEqual(CacheCurves, newCurves) Then
            If ProximityCache Then
                SliderParams = RemapParamsByProximity(CacheCurves, SliderParams, newCurves)
            ElseIf Not PreserveChanges Then
                SliderParams.Clear()
            End If
            CacheCurves = newCurves
        End If

        Curves = newCurves

        While SliderParams.Count < Curves.Count
            SliderParams.Add(0.5R)
        End While
        If Not PreserveChanges AndAlso SliderParams.Count > Curves.Count Then
            SliderParams.RemoveRange(Curves.Count, SliderParams.Count - Curves.Count)
        End If

        If EditIndex >= Curves.Count Then CloseSliderTextBoxIfAny()

        Points.Clear()
        Dim outVals As New List(Of Double)(Curves.Count)
        For i As Integer = 0 To Curves.Count - 1
            Points.Add(PointOnCurveAtNormalized(Curves(i), SliderParams(i)))
            outVals.Add(DisplayValue(i))
        Next

        DA.SetDataList(0, Points)
        DA.SetDataList(1, outVals)
    End Sub

#End Region

#Region "Preview"

    ''' <summary>Draw a camera-facing tick perpendicular to the curve at normalized t; half-length given in pixels.</summary>
    Private Shared Sub DrawCurveTick(args As IGH_PreviewArgs, crv As Curve, tNorm As Double, halfLenPx As Double, col As Color, thickness As Integer,
                                     Optional tickLabel As String = Nothing)
        Dim tRaw As Double = crv.Domain.ParameterAt(Math.Max(0.0R, Math.Min(1.0R, tNorm)))
        Dim pt As Point3d = crv.PointAt(tRaw)
        If Not pt.IsValid Then Return
        Dim tan As Vector3d = crv.TangentAt(tRaw)
        Dim camZ As Vector3d = args.Viewport.CameraZ
        Dim tickDir As Vector3d = Vector3d.CrossProduct(tan, camZ)
        If Not tickDir.Unitize() Then
            tickDir = Vector3d.CrossProduct(tan, args.Viewport.CameraY)
            If Not tickDir.Unitize() Then Return
        End If
        ' Convert pixel length to world units at this point.
        Dim pxPerUnit As Double = 0
        args.Viewport.GetWorldToScreenScale(pt, pxPerUnit)
        If pxPerUnit <= 0 Then Return
        Dim halfLen As Double = halfLenPx / pxPerUnit
        Dim a As Point3d = pt + tickDir * halfLen
        Dim b As Point3d = pt - tickDir * halfLen
        args.Display.DrawLine(New Line(a, b), col, thickness)

        If tickLabel IsNot Nothing Then
            Dim screenPt As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(pt)
            args.Display.Draw2dText(tickLabel, col, New Rhino.Geometry.Point2d(screenPt.X + 6.0R, screenPt.Y - 6.0R), True, 10)
        End If
    End Sub

    Private Shared Function FormatTickValue(v As Double) As String
        Return v.ToString("0.###", Globalization.CultureInfo.InvariantCulture)
    End Function

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        If Curves.Count = 0 Then Return

        Dim selected As Boolean = Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected
        Dim col As Color = If(selected, args.WireColour_Selected, args.WireColour)

        For i As Integer = 0 To Curves.Count - 1
            Dim crv As Curve = Curves(i)
            If crv Is Nothing Then Continue For

            Dim startLabel As String = If(selected, FormatTickValue(DisplayValueAtNormalized(i, 0.0R)), Nothing)
            DrawCurveTick(args, crv, 0.0R, 8.0R, col, 2, startLabel)

            Dim endLabel As String = If(selected, FormatTickValue(DisplayValueAtNormalized(i, 1.0R)), Nothing)
            DrawCurveTick(args, crv, 1.0R, 8.0R, col, 2, endLabel)

            ' Snap value markers (shorter than the end ticks).
            For Each ts As Double In SnapParamsForCurve(i)
                Dim snapLbl As String = If(selected, FormatTickValue(DisplayValueAtNormalized(i, ts)), Nothing)
                DrawCurveTick(args, crv, ts, 4.0R, col, 1, snapLbl)
            Next

            Dim pt As Point3d = If(i < Points.Count, Points(i), Point3d.Unset)
            If Not pt.IsValid Then Continue For

            args.Display.DrawPoint(pt, PointStyle.RoundControlPoint, 5, col)

            Dim v As Double = DisplayValue(i)
            Dim label As String = v.ToString("0.###", Globalization.CultureInfo.InvariantCulture)
            Dim screenPt As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(pt)
            args.Display.Draw2dText(label, col, New Rhino.Geometry.Point2d(screenPt.X, screenPt.Y - 14.0R), True, 14)
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim bb As BoundingBox = BoundingBox.Empty
            For Each p As Point3d In Points
                If p.IsValid Then bb.Union(p)
            Next
            If bb.IsValid Then bb.Inflate(1.0R)
            Return bb
        End Get
    End Property

#End Region

#Region "Write/Read"

    Public Overrides Function Write(writer As GH_IO.Serialization.GH_IWriter) As Boolean
        writer.SetBoolean("CS_Preserve", PreserveChanges)
        writer.SetBoolean("CS_Proximity", ProximityCache)
        writer.SetBoolean("CS_Real", ShowRealValue)
        writer.SetBoolean("CS_CustomDomain", CustomDomain)
        writer.SetBoolean("CS_Snap", SnapValues)
        writer.SetInt32("CS_Count", SliderParams.Count)
        For i As Integer = 0 To SliderParams.Count - 1
            writer.SetDouble("CS_Param", i, SliderParams(i))
        Next
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(reader As GH_IO.Serialization.GH_IReader) As Boolean
        Dim preserve As Boolean = True
        reader.TryGetBoolean("CS_Preserve", preserve)
        PreserveChanges = preserve

        Dim prox As Boolean = False
        reader.TryGetBoolean("CS_Proximity", prox)
        ProximityCache = prox

        Dim realV As Boolean = True
        reader.TryGetBoolean("CS_Real", realV)
        ShowRealValue = realV

        Dim custom As Boolean = False
        reader.TryGetBoolean("CS_CustomDomain", custom)
        CustomDomain = custom

        Dim snap As Boolean = False
        reader.TryGetBoolean("CS_Snap", snap)
        SnapValues = snap

        ' Register the optional D/S inputs before MyBase.Read so archived param data/sources map onto them.
        SyncOptionalInputs()

        SliderParams.Clear()
        Dim n As Integer = 0
        If reader.TryGetInt32("CS_Count", n) Then
            For i As Integer = 0 To n - 1
                Dim d As Double = 0.5R
                reader.TryGetDouble("CS_Param", i, d)
                SliderParams.Add(Math.Max(0.0R, Math.Min(1.0R, d)))
            Next
        End If
        Return MyBase.Read(reader)
    End Function

#End Region

End Class

Public Class CurveSliderCompAtt
    Inherits Grasshopper.Kernel.Attributes.GH_ComponentAttributes

    Private MyOwner As CurveSliderComp

    Sub New(owner As CurveSliderComp)
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

''' <summary>In-session undo for slider params / flags.</summary>
Public Class CurveSliderUndo
    Inherits Grasshopper.Kernel.Undo.GH_UndoAction

    Private ReadOnly _ownerId As Guid
    Private _params As List(Of Double)
    Private _preserve As Boolean
    Private _proximity As Boolean
    Private _real As Boolean
    Private _customDomain As Boolean
    Private _snapValues As Boolean

    Sub New(owner As CurveSliderComp)
        _ownerId = owner.InstanceGuid
        _params = New List(Of Double)(owner.SliderParams)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _real = owner.ShowRealValue
        _customDomain = owner.CustomDomain
        _snapValues = owner.SnapValues
    End Sub

    Protected Overrides Sub Internal_Undo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Protected Overrides Sub Internal_Redo(doc As GH_Document)
        SwapState(doc)
    End Sub

    Private Sub SwapState(doc As GH_Document)
        Dim comp As CurveSliderComp = TryCast(doc.FindObject(_ownerId, True), CurveSliderComp)
        If comp Is Nothing Then Return
        Dim curParams As New List(Of Double)(comp.SliderParams)
        Dim curPreserve As Boolean = comp.PreserveChanges
        Dim curProximity As Boolean = comp.ProximityCache
        Dim curReal As Boolean = comp.ShowRealValue
        Dim curCustom As Boolean = comp.CustomDomain
        Dim curSnap As Boolean = comp.SnapValues
        comp.SetStateFromUndo(_params, _preserve, _proximity, _real, _customDomain, _snapValues)
        _params = curParams
        _preserve = curPreserve
        _proximity = curProximity
        _real = curReal
        _customDomain = curCustom
        _snapValues = curSnap
    End Sub

End Class

''' <summary>Viewport drag/click on the slider dot (enabled only while the component is selected on canvas).</summary>
Public Class CurveSliderMouse
    Inherits Rhino.UI.MouseCallback

    Private ReadOnly Comp As CurveSliderComp

    Sub New(owner As CurveSliderComp)
        Comp = owner
    End Sub

    ''' <summary>Pixel pick radius around the slider dot.</summary>
    Private Const PickRadiusPx As Double = 14.0R
    ''' <summary>Beyond this many pixels the gesture counts as a drag along the curve, not a click.</summary>
    Private Const ClickSlopPx As Double = 4.0R

    Private _dragIndex As Integer = -1
    Private _dragged As Boolean
    Private _downViewport As Drawing.Point
    ''' <summary>Undo snapshot captured at mouse-down; registered on mouse-up only if a drag happened.</summary>
    Private _pendingUndo As CurveSliderUndo = Nothing

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        _dragIndex = -1
        _dragged = False
        _pendingUndo = Nothing
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If e.View Is Nothing Then Exit Sub

        Comp.CloseSliderTextBoxIfAny()

        Dim vp As RhinoViewport = e.View.ActiveViewport
        If vp Is Nothing Then Exit Sub

        Dim hit As Integer = -1
        Dim bestDist As Double = Double.PositiveInfinity
        For i As Integer = 0 To Comp.Points.Count - 1
            Dim wpt As Point3d = Comp.Points(i)
            If Not wpt.IsValid Then Continue For
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

        _dragIndex = hit
        _downViewport = e.ViewportPoint
        _pendingUndo = New CurveSliderUndo(Comp)
        ' The slider dot is ours; swallow the press so Rhino does not start a selection window.
        e.Cancel = True
    End Sub

    ''' <summary>Normalized parameter of the curve point closest to the viewport pick ray.</summary>
    Private Shared Function TryParamFromViewportRay(crv As Curve, view As Rhino.Display.RhinoView, viewportPt As Drawing.Point, ByRef tNorm As Double) As Boolean
        If crv Is Nothing OrElse view Is Nothing Then Return False
        Dim ray As Line = Nothing
        If Not view.ActiveViewport.GetFrustumLine(CDbl(viewportPt.X), CDbl(viewportPt.Y), ray) Then Return False
        Dim ptCrv As Point3d = Nothing
        Dim ptRay As Point3d = Nothing
        Using lc As New LineCurve(ray)
            If Not crv.ClosestPoints(lc, ptCrv, ptRay) Then Return False
        End Using
        Dim tRaw As Double
        If Not crv.ClosestPoint(ptCrv, tRaw) Then Return False
        tNorm = crv.Domain.NormalizedParameterAt(tRaw)
        Return True
    End Function

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)
        If _dragIndex < 0 Then Exit Sub
        If Comp Is Nothing OrElse _dragIndex >= Comp.Curves.Count Then Exit Sub

        If Not _dragged Then
            Dim dx As Double = CDbl(e.ViewportPoint.X) - CDbl(_downViewport.X)
            Dim dy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_downViewport.Y)
            If (dx * dx + dy * dy) < (ClickSlopPx * ClickSlopPx) Then
                e.Cancel = True
                Exit Sub
            End If
            _dragged = True
        End If

        Dim t As Double
        If TryParamFromViewportRay(Comp.Curves(_dragIndex), e.View, e.ViewportPoint, t) Then
            t = ApplySnapIfClose(_dragIndex, t, e.View)
            Comp.DragSliderParam(_dragIndex, t)
            Try
                Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
            Catch
            End Try
        End If
        e.Cancel = True
    End Sub

    ''' <summary>Screen-space snap radius while dragging (pixels).</summary>
    Private Const SnapRadiusPx As Double = 8.0R

    ''' <summary>Stick the drag parameter to the nearest snap value when within the pixel radius.</summary>
    Private Function ApplySnapIfClose(index As Integer, t As Double, view As Rhino.Display.RhinoView) As Double
        If Comp Is Nothing OrElse view Is Nothing Then Return t
        Dim snaps As List(Of Double) = Comp.SnapParamsForCurve(index)
        If snaps.Count = 0 Then Return t
        Dim crv As Curve = Comp.Curves(index)
        If crv Is Nothing Then Return t

        Dim vp As RhinoViewport = view.ActiveViewport
        Dim dragPt As Point3d = crv.PointAt(crv.Domain.ParameterAt(Math.Max(0.0R, Math.Min(1.0R, t))))
        Dim dragScreen As Rhino.Geometry.Point2d = vp.WorldToClient(dragPt)

        Dim bestT As Double = t
        Dim bestDist As Double = Double.PositiveInfinity
        For Each ts As Double In snaps
            Dim sp As Point3d = crv.PointAt(crv.Domain.ParameterAt(ts))
            Dim ss As Rhino.Geometry.Point2d = vp.WorldToClient(sp)
            Dim dx As Double = ss.X - dragScreen.X
            Dim dy As Double = ss.Y - dragScreen.Y
            Dim d2 As Double = dx * dx + dy * dy
            If d2 < bestDist Then
                bestDist = d2
                bestT = ts
            End If
        Next
        If bestDist <= SnapRadiusPx * SnapRadiusPx Then Return bestT
        Return t
    End Function

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseUp(e)
        Dim ix As Integer = _dragIndex
        Dim dragged As Boolean = _dragged
        Dim undo As CurveSliderUndo = _pendingUndo
        _dragIndex = -1
        _dragged = False
        _pendingUndo = Nothing
        If ix < 0 Then Exit Sub
        If Comp Is Nothing Then Exit Sub
        If ix >= Comp.Curves.Count Then Exit Sub

        If dragged Then
            If undo IsNot Nothing Then Comp.RecordUndoEvent("Curve Slider Drag", undo)
            e.Cancel = True
            Exit Sub
        End If

        ' Clean click: open the numeric entry float pre-filled with the current display value.
        Comp.EditIndex = ix
        Dim current As String = Comp.DisplayValue(ix).ToString("0.######", Globalization.CultureInfo.InvariantCulture)
        Comp.SliderTextBox = New FormCurveSliderBox(Control.MousePosition, Comp, ix, current)
        e.Cancel = True
    End Sub

End Class

''' <summary>Floating single-line numeric entry for a curve slider; Enter commits, Escape / click elsewhere dismisses.</summary>
Public Class FormCurveSliderBox
    Inherits System.Windows.Forms.Form

    Private Shared _activeInstance As FormCurveSliderBox

    ''' <summary>Rhino MouseCallback route: dismiss when the viewport is pressed while this float has focus.</summary>
    Friend Shared Sub RequestDismissFromBackdropMouse()
        Dim f As FormCurveSliderBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed Then Return
        f.TryDismissFromOutsideRhinoGesture()
    End Sub

    Private Comp As CurveSliderComp
    Private ReadOnly SlotIndex As Integer
    Private _committing As Boolean
    Private _outsideDismissReady As Boolean
    Private _suppressBackdropDismissUntil As Integer
    Private _hookedCanvas As GH_Canvas

    Sub New(screenLocation As Drawing.Point, owner As CurveSliderComp, index As Integer, initialText As String)
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
        Dim p As FormCurveSliderBox = _activeInstance
        _activeInstance = Nothing
        p.SilentCloseLeakWithoutCancelPending()
    End Sub

    Private Sub SilentCloseLeakWithoutCancelPending()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If Comp IsNot Nothing Then Comp.ForgetFloatingSliderTextBox()
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
        Dim self As FormCurveSliderBox = Me
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

    ''' <summary>Cancel value edit (Escape, click outside, lost activation).</summary>
    Friend Sub DismissWithoutCommit()
        If _committing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        If Comp IsNot Nothing Then Comp.CancelPendingValueInput()
        Close()
    End Sub

    Private Sub FormCurveSliderBox_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
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

    Private Sub FormCurveSliderBox_Deactivate(sender As Object, e As EventArgs) Handles MyBase.Deactivate
        If Not _outsideDismissReady OrElse _committing Then Return
        If Environment.TickCount < _suppressBackdropDismissUntil Then Return
        DismissWithoutCommit()
    End Sub

    Private Sub TryCommitEntry()
        If _committing OrElse Comp Is Nothing OrElse TextBox1 Is Nothing Then Return
        _committing = True
        DetachGrasshopperCanvasDismissHookInternal()
        Try
            Comp.CommitSliderValue(SlotIndex, TextBox1.Text)
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
        If e.KeyChar = ChrW(13) OrElse e.KeyChar = ChrW(10) Then
            e.Handled = True
            TryCommitEntry()
        End If
    End Sub

    Private Sub FormCurveSliderBox_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        DetachGrasshopperCanvasDismissHookInternal()
    End Sub

    Private Sub FormCurveSliderBox_FormClosed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If ReferenceEquals(_activeInstance, Me) Then
            _activeInstance = Nothing
        End If
        RefreshBackdropMouseCallbackListening()
        If Comp IsNot Nothing Then Comp.ForgetFloatingSliderTextBox()
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
        Me.TextBox1.Size = New System.Drawing.Size(100, 20)
        Me.TextBox1.TabIndex = 0
        Me.TextBox1.Multiline = False
        Me.TextBox1.AcceptsReturn = False
        '
        'FormCurveSliderBox
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(100, 20)
        Me.Controls.Add(Me.TextBox1)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
        Me.StartPosition = FormStartPosition.Manual
        Me.MaximumSize = New System.Drawing.Size(100, 20)
        Me.MinimumSize = New System.Drawing.Size(100, 20)
        Me.Name = "FormCurveSliderBox"
        Me.Text = "CurveSlider"
        Me.Owner = Grasshopper.Instances.DocumentEditor
        Me.ResumeLayout(False)
        Me.PerformLayout()
    End Sub

    Friend WithEvents TextBox1 As TextBox
End Class
