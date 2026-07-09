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

Public Class CurveSliderComp
    Inherits GH_Component
    Implements IGH_VariableParameterComponent

    ''' <summary>Optional inputs exposed via canvas ZUI (+) in fixed order after the curve input.</summary>
    Private Enum ZuiOptionalKind
        None = -1
        Active = 0
        RealValues = 1
        CustomDomain = 2
        SnapValues = 3
        SnappingTicks = 4
        StartingPosition = 5
        LockUnselected = 6
        PreserveChanges = 7
        ProximityCache = 8
        ClearCache = 9
    End Enum

    ''' <summary>Optional outputs exposed via canvas ZUI (+) after P and t.</summary>
    Private Enum ZuiOptionalOutputKind
        None = -1
        NormalizedValue = 0
        CurveDomain = 1
    End Enum

    Private Shared ReadOnly ZuiCanonicalOrder As ZuiOptionalKind() = {
        ZuiOptionalKind.Active,
        ZuiOptionalKind.RealValues,
        ZuiOptionalKind.CustomDomain,
        ZuiOptionalKind.SnapValues,
        ZuiOptionalKind.SnappingTicks,
        ZuiOptionalKind.StartingPosition,
        ZuiOptionalKind.LockUnselected,
        ZuiOptionalKind.PreserveChanges,
        ZuiOptionalKind.ProximityCache,
        ZuiOptionalKind.ClearCache
    }

    Private Shared ReadOnly ZuiOutputOrder As ZuiOptionalOutputKind() = {
        ZuiOptionalOutputKind.NormalizedValue,
        ZuiOptionalOutputKind.CurveDomain
    }

    ''' <summary>Per-curve settings resolved from optional tree inputs (paths match curve input C).</summary>
    Friend Structure CurveSliderSlotSettings
        Public Active As Boolean
        Public ShowRealValue As Boolean
        Public CustomInterval As Interval
        Public HasCustomInterval As Boolean
        Public SnapDisplayValues As List(Of Double)
        Public SnapTickStep As Double
        Public HasSnapTickStep As Boolean
    End Structure

    Friend SlotSettings As CurveSliderSlotSettings()

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
        pManager.AddCurveParameter("Curve", "C", "Curve(s) to place a slider point on.", GH_ParamAccess.tree)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddPointParameter("Point", "P", "Slider point on each curve.", GH_ParamAccess.tree)
        pManager.AddNumberParameter("Value", "t", "Slider value per curve (normalized 0-1, real curve parameter, or custom-domain value per right-click settings).", GH_ParamAccess.tree)
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New CurveSliderCompAtt(Me)
    End Sub

    Public Overrides Sub AddedToDocument(document As GH_Document)
        MyBase.AddedToDocument(document)
        VariableParameterMaintenance()
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

        Dim realVals As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Real values", AddressOf Menu_RealValues, True, MenuBoolChecked(ShowRealValue, ZuiOptionalKind.RealValues))
        realVals.ToolTipText = "Show and enter values in the curve's real parameter domain instead of normalized 0-1. Ignored while a custom domain is set."

        Dim customDom As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Custom domain", AddressOf Menu_CustomDomain, True, Me.CustomDomain)
        customDom.ToolTipText = "Adds a D input: slider values are shown, entered and output in that domain (0-1 remapped)."

        Dim snapVals As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Snapping values", AddressOf Menu_SnapValues, True, Me.SnapValues)
        snapVals.ToolTipText = "Adds an S input (list of values in the current display units): dragging sticks to those values, shown as short ticks on the curve."

        Dim snapTicks As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Snapping ticks", AddressOf Menu_SnappingTicks, True, HasZuiInput(ZuiOptionalKind.SnappingTicks) OrElse Me.SnappingTicks)
        snapTicks.ToolTipText = "While dragging, stick the slider to tick steps. Adds a T input for a fixed step (e.g. 5 → 0,5,10…); leave T empty to snap to the zoom-adaptive ruler."

        Dim startPos As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Starting position", AddressOf Menu_StartingPosition, True, Me.StartingPosition)
        startPos.ToolTipText = "Adds a Sp input for the initial slider location per curve. Units follow Real values / Custom domain when those are on; otherwise normalized 0-1. Viewport edits are preserved."

        Menu_AppendSeparator(menu)

        Dim lockUnsel As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Lock unselected", AddressOf Menu_LockUnselected, True, MenuBoolChecked(LockUnselected, ZuiOptionalKind.LockUnselected))
        lockUnsel.ToolTipText = "When on, the slider can be dragged or edited only while this component is selected on the Grasshopper canvas."

        Dim preserve As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Preserve changes", AddressOf Menu_PreserveChanges, True, MenuBoolChecked(PreserveChanges, ZuiOptionalKind.PreserveChanges))
        preserve.ToolTipText = "Keep slider values (per item index) when upstream curves move or change."

        Dim proximity As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, MenuBoolChecked(ProximityCache, ZuiOptionalKind.ProximityCache))
        proximity.ToolTipText = "When the curve list changes, re-attach each slider to the nearest new curve by point location instead of the list index."

        Dim cc As Windows.Forms.ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Reset all slider values to the curve midpoint."
    End Sub

    Private Function StartingPositionInputDescription() As String
        If CustomDomain Then
            Return "Initial slider value per curve in the custom domain (tree paths match the curve input). Viewport edits are preserved; changing this input does not reset cached slider values."
        ElseIf ShowRealValue Then
            Return "Initial slider value per curve in the curve's real parameter domain (tree paths match the curve input). Viewport edits are preserved; changing this input does not reset cached slider values."
        Else
            Return "Normalized curve parameter (0-1) for the initial slider location per curve (tree paths match the curve input). Viewport edits are preserved; changing this input does not reset cached slider values."
        End If
    End Function

    Private Sub RefreshStartingPositionInputDescription()
        Dim spIx As Integer = FindStartingPositionInputIndex()
        If spIx < 0 Then Return
        Params.Input(spIx).Description = StartingPositionInputDescription()
    End Sub

    Private Sub Menu_RealValues()
        RecordUndoEvent("Curve Slider Values", New CurveSliderUndo(Me))
        ShowRealValue = Not ShowRealValue
        RefreshStartingPositionInputDescription()
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub Menu_CustomDomain()
        RecordUndoEvent("Curve Slider Domain", New CurveSliderUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.CustomDomain, Not CustomDomain)
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_SnapValues()
        RecordUndoEvent("Curve Slider Snapping", New CurveSliderUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.SnapValues, Not SnapValues)
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_SnappingTicks()
        RecordUndoEvent("Curve Slider Snapping Ticks", New CurveSliderUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.SnappingTicks, Not (HasZuiInput(ZuiOptionalKind.SnappingTicks) OrElse SnappingTicks))
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Private Sub Menu_StartingPosition()
        RecordUndoEvent("Curve Slider Starting Position", New CurveSliderUndo(Me))
        SetZuiKindEnabled(ZuiOptionalKind.StartingPosition, Not StartingPosition)
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_LockUnselected()
        RecordUndoEvent("Curve Slider Lock Unselected", New CurveSliderUndo(Me))
        LockUnselected = Not LockUnselected
        SyncMouse()
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Curve Slider Preserve", New CurveSliderUndo(Me))
        PreserveChanges = Not PreserveChanges
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Curve Slider Proximity", New CurveSliderUndo(Me))
        ProximityCache = Not ProximityCache
        Me.ExpireSolution(True)
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

#Region "Optional inputs / ZUI"

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

    Private Function FindTickStepInputIndex() As Integer
        Return FindInputIndexByNick("T")
    End Function

    Private Function FindStartingPositionInputIndex() As Integer
        Return FindInputIndexByNick("Sp")
    End Function

    Private Function FindRealValuesInputIndex() As Integer
        Return FindInputIndexByNick("Rv")
    End Function

    Private Function FindActiveInputIndex() As Integer
        Return FindInputIndexByNick("Ac")
    End Function

    Private Function FindOutputIndexByNick(nick As String) As Integer
        If Params Is Nothing Then Return -1
        For i As Integer = 0 To Params.Output.Count - 1
            If String.Equals(Params.Output(i).NickName, nick, StringComparison.OrdinalIgnoreCase) Then Return i
        Next
        Return -1
    End Function

    Private Shared Function NickNameForZuiKind(kind As ZuiOptionalKind) As String
        Select Case kind
            Case ZuiOptionalKind.Active : Return "Ac"
            Case ZuiOptionalKind.RealValues : Return "Rv"
            Case ZuiOptionalKind.CustomDomain : Return "D"
            Case ZuiOptionalKind.SnapValues : Return "S"
            Case ZuiOptionalKind.SnappingTicks : Return "T"
            Case ZuiOptionalKind.StartingPosition : Return "Sp"
            Case ZuiOptionalKind.LockUnselected : Return "Lu"
            Case ZuiOptionalKind.PreserveChanges : Return "Pr"
            Case ZuiOptionalKind.ProximityCache : Return "Px"
            Case ZuiOptionalKind.ClearCache : Return "Cc"
            Case Else : Return String.Empty
        End Select
    End Function

    Private Shared Function NickNameForZuiOutput(kind As ZuiOptionalOutputKind) As String
        Select Case kind
            Case ZuiOptionalOutputKind.NormalizedValue : Return "u"
            Case ZuiOptionalOutputKind.CurveDomain : Return "Dom"
            Case Else : Return String.Empty
        End Select
    End Function

    Private Function HasZuiInput(kind As ZuiOptionalKind) As Boolean
        If kind = ZuiOptionalKind.None Then Return False
        Return FindInputIndexByNick(NickNameForZuiKind(kind)) >= 0
    End Function

    Private Function HasZuiOutput(kind As ZuiOptionalOutputKind) As Boolean
        If kind = ZuiOptionalOutputKind.None Then Return False
        Return FindOutputIndexByNick(NickNameForZuiOutput(kind)) >= 0
    End Function

    Private Function NextZuiKindToInsert() As ZuiOptionalKind
        For Each kind As ZuiOptionalKind In ZuiCanonicalOrder
            If Not HasZuiInput(kind) Then Return kind
        Next
        Return ZuiOptionalKind.None
    End Function

    Private Function NextZuiOutputToInsert() As ZuiOptionalOutputKind
        For Each kind As ZuiOptionalOutputKind In ZuiOutputOrder
            If Not HasZuiOutput(kind) Then Return kind
        Next
        Return ZuiOptionalOutputKind.None
    End Function

    Private Function CanonicalInsertIndex(kind As ZuiOptionalKind) As Integer
        Dim idx As Integer = 1
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If k = kind Then Return idx
            If HasZuiInput(k) Then idx += 1
        Next
        Return Math.Max(1, Me.Params.Input.Count)
    End Function

    Private Function CanonicalOutputInsertIndex(kind As ZuiOptionalOutputKind) As Integer
        Dim idx As Integer = 2
        For Each k As ZuiOptionalOutputKind In ZuiOutputOrder
            If k = kind Then Return idx
            If HasZuiOutput(k) Then idx += 1
        Next
        Return Math.Max(2, Me.Params.Output.Count)
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

    Private Function CreateTickStepParam() As Grasshopper.Kernel.Parameters.Param_Number
        Return New Grasshopper.Kernel.Parameters.Param_Number With {
            .Optional = True,
            .Name = "Tick step",
            .NickName = "T",
            .Description = "Fixed tick step per curve in display units (tree matches C). Wire to enable snapping; leave value empty to use zoom-adaptive ruler ticks.",
            .Access = GH_ParamAccess.tree
        }
    End Function

    Private Function CreateZuiParam(kind As ZuiOptionalKind) As IGH_Param
        Select Case kind
            Case ZuiOptionalKind.Active
                Return CreateBoolZuiParam("Active", "Ac",
                    "When true, viewport picking is enabled for that curve (overrides Lock unselected). Tree paths match C.",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.RealValues
                Return CreateBoolZuiParam("Real values", "Rv",
                    "When true, show and enter values in the curve's real parameter domain instead of normalized 0-1 (per curve; tree matches C). Ignored while a custom domain is set.",
                    GH_ParamAccess.tree)
            Case ZuiOptionalKind.CustomDomain
                Return New Grasshopper.Kernel.Parameters.Param_Interval With {
                    .Optional = True,
                    .Name = "Domain",
                    .NickName = "D",
                    .Description = "Custom value domain per curve (tree matches C); values remapped from the curve's 0-1.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.SnapValues
                Return New Grasshopper.Kernel.Parameters.Param_Number With {
                    .Optional = True,
                    .Name = "Snap values",
                    .NickName = "S",
                    .Description = "Snap values per curve in display units (tree paths match C; all numbers on a branch apply to curves on that path).",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.SnappingTicks
                Return CreateTickStepParam()
            Case ZuiOptionalKind.StartingPosition
                Return New Grasshopper.Kernel.Parameters.Param_Number With {
                    .Optional = True,
                    .Name = "Starting position",
                    .NickName = "Sp",
                    .Description = StartingPositionInputDescription(),
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalKind.LockUnselected
                Return CreateBoolZuiParam("Lock unselected", "Lu",
                    "When true, viewport picking works only while this component is selected on the Grasshopper canvas. Overridden by Active when that input is present.")
            Case ZuiOptionalKind.PreserveChanges
                Return CreateBoolZuiParam("Preserve changes", "Pr",
                    "When true, keep slider values when upstream curves move or change.")
            Case ZuiOptionalKind.ProximityCache
                Return CreateBoolZuiParam("Proximity cache", "Px",
                    "When true, re-attach each slider to the nearest new curve when the curve list changes.")
            Case ZuiOptionalKind.ClearCache
                Return CreateBoolZuiParam("Clear cache", "Cc",
                    "Pulse true to reset all slider values to the starting position (rising edge only).")
        End Select
        Return Nothing
    End Function

    Private Function CreateZuiOutputParam(kind As ZuiOptionalOutputKind) As IGH_Param
        Select Case kind
            Case ZuiOptionalOutputKind.NormalizedValue
                Return New Grasshopper.Kernel.Parameters.Param_Number With {
                    .Name = "Normalized",
                    .NickName = "u",
                    .Description = "Normalized slider parameter (0-1) per curve, independent of the display/output units on t.",
                    .Access = GH_ParamAccess.tree
                }
            Case ZuiOptionalOutputKind.CurveDomain
                Return New Grasshopper.Kernel.Parameters.Param_Interval With {
                    .Name = "Curve domain",
                    .NickName = "Dom",
                    .Description = "Curve parameter domain per input curve (pass-through from geometry).",
                    .Access = GH_ParamAccess.tree
                }
        End Select
        Return Nothing
    End Function

    Private Sub SyncFeatureFlagsFromInputs()
        CustomDomain = HasZuiInput(ZuiOptionalKind.CustomDomain)
        SnapValues = HasZuiInput(ZuiOptionalKind.SnapValues)
        SnappingTicks = HasZuiInput(ZuiOptionalKind.SnappingTicks)
        StartingPosition = HasZuiInput(ZuiOptionalKind.StartingPosition)
    End Sub

    Private Sub ApplyZuiInputLayout()
        SyncFeatureFlagsFromInputs()

        Dim spIx As Integer = FindStartingPositionInputIndex()
        If spIx >= 0 Then Params.Input(spIx).Description = StartingPositionInputDescription()
    End Sub

    Private Sub SetZuiKindEnabled(kind As ZuiOptionalKind, enabled As Boolean)
        If kind = ZuiOptionalKind.None Then Return
        If enabled Then
            If HasZuiInput(kind) Then Return
            Dim param As IGH_Param = CreateZuiParam(kind)
            If param Is Nothing Then Return
            Me.Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
        Else
            Dim nick As String = NickNameForZuiKind(kind)
            Dim ix As Integer = FindInputIndexByNick(nick)
            If ix < 0 Then Return
            Dim p As IGH_Param = Me.Params.Input(ix)
            p.RemoveAllSources()
            Me.Params.UnregisterInputParameter(p)
        End If
        SyncFeatureFlagsFromInputs()
        VariableParameterMaintenance()
        Me.Params.OnParametersChanged()
        RefreshStartingPositionInputDescription()
    End Sub

    ''' <summary>Backward-compatible name used by undo/read paths.</summary>
    Friend Sub SyncOptionalInputs()
        SyncOptionalInputsFromFlags()
        Me.Params.OnParametersChanged()
    End Sub

    Private Sub SyncOptionalInputsFromFlags()
        EnsureZuiMatchesFlag(ZuiOptionalKind.CustomDomain, CustomDomain)
        EnsureZuiMatchesFlag(ZuiOptionalKind.SnapValues, SnapValues)
        EnsureZuiMatchesFlag(ZuiOptionalKind.SnappingTicks, SnappingTicks)
        EnsureZuiMatchesFlag(ZuiOptionalKind.StartingPosition, StartingPosition)
        VariableParameterMaintenance()
    End Sub

    Private Function ZuiInputWired(ix As Integer) As Boolean
        If ix < 0 OrElse Params Is Nothing Then Return False
        Dim p As IGH_Param = Params.Input(ix)
        Return p IsNot Nothing AndAlso p.SourceCount > 0
    End Function

    Private Function MenuBoolChecked(defaultValue As Boolean, kind As ZuiOptionalKind) As Boolean
        Dim ix As Integer = FindInputIndexByNick(NickNameForZuiKind(kind))
        If ix >= 0 AndAlso ZuiInputWired(ix) Then Return ReadBoolInputVolatile(ix, defaultValue)
        Return defaultValue
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
        Else
            target = defaultIfUnwired
        End If
    End Sub

    Private Sub ApplyZuiBooleanInputs(DA As IGH_DataAccess)
        ApplyBoolInput(DA, FindInputIndexByNick("Lu"), LockUnselected, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Pr"), PreserveChanges, True)
        ApplyBoolInput(DA, FindInputIndexByNick("Px"), ProximityCache, False)

        Dim ccIx As Integer = FindInputIndexByNick("Cc")
        If ccIx >= 0 AndAlso Params.Input(ccIx).SourceCount > 0 Then
            Dim pulse As Boolean = False
            If DA.GetData(ccIx, pulse) Then
                If pulse AndAlso Not _clearCacheInputPrev Then
                    ClearSliderCacheInternal()
                End If
                _clearCacheInputPrev = pulse
            End If
        Else
            _clearCacheInputPrev = False
        End If
    End Sub

    Private Sub ClearSliderCacheInternal()
        SliderParams.Clear()
        CacheCurves = Nothing
        CloseSliderTextBoxIfAny()
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

    Friend Function IsCurveActiveForViewport(index As Integer) As Boolean
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
        Dim acIx As Integer = FindActiveInputIndex()
        If acIx >= 0 Then Return True
        Dim luIx As Integer = FindInputIndexByNick("Lu")
        If luIx >= 0 Then
            Return Not ReadBoolInputVolatile(luIx, True) OrElse (Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected)
        End If
        Return Not LockUnselected OrElse (Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected)
    End Function

    Private Sub EnsureZuiMatchesFlag(kind As ZuiOptionalKind, shouldHave As Boolean)
        If shouldHave Then
            If Not HasZuiInput(kind) Then
                Dim param As IGH_Param = CreateZuiParam(kind)
                If param IsNot Nothing Then Me.Params.RegisterInputParam(param, CanonicalInsertIndex(kind))
            End If
        ElseIf HasZuiInput(kind) Then
            Dim ix As Integer = FindInputIndexByNick(NickNameForZuiKind(kind))
            If ix >= 0 Then
                Dim p As IGH_Param = Me.Params.Input(ix)
                p.RemoveAllSources()
                Me.Params.UnregisterInputParameter(p)
            End If
        End If
    End Sub

    Private Sub ApplyRealValuesFromInput(DA As IGH_DataAccess)
        ' Per-curve Rv is resolved in BuildSlotSettings.
    End Sub

    Private Function KindToInsertAt(index As Integer) As ZuiOptionalKind
        If index < 1 OrElse index > Params.Input.Count Then Return ZuiOptionalKind.None
        If index = Params.Input.Count Then Return NextZuiKindToInsert()
        Dim targetSlot As Integer = index - 1
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
        If index < 1 OrElse index >= Params.Input.Count Then Return False
        Dim nick As String = Params.Input(index).NickName
        For Each k As ZuiOptionalKind In ZuiCanonicalOrder
            If String.Equals(nick, NickNameForZuiKind(k), StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Private Function KindToInsertOutputAt(index As Integer) As ZuiOptionalOutputKind
        If index < 2 OrElse index > Params.Output.Count Then Return ZuiOptionalOutputKind.None
        If index = Params.Output.Count Then Return NextZuiOutputToInsert()
        Dim targetSlot As Integer = index - 2
        Dim canonSlot As Integer = 0
        For Each k As ZuiOptionalOutputKind In ZuiOutputOrder
            If canonSlot = targetSlot Then
                If Not HasZuiOutput(k) Then Return k
                Return ZuiOptionalOutputKind.None
            End If
            canonSlot += 1
        Next
        Return ZuiOptionalOutputKind.None
    End Function

    Private Function IsRemovableZuiOutput(index As Integer) As Boolean
        If index < 2 OrElse index >= Params.Output.Count Then Return False
        Dim nick As String = Params.Output(index).NickName
        For Each k As ZuiOptionalOutputKind In ZuiOutputOrder
            If String.Equals(nick, NickNameForZuiOutput(k), StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

#End Region

#Region "Variable parameters (canvas ZUI)"

    Public Function CanInsertParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanInsertParameter
        If side = GH_ParameterSide.Input Then Return KindToInsertAt(index) <> ZuiOptionalKind.None
        If side = GH_ParameterSide.Output Then Return KindToInsertOutputAt(index) <> ZuiOptionalOutputKind.None
        Return False
    End Function

    Public Function CanRemoveParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanRemoveParameter
        If side = GH_ParameterSide.Input Then Return IsRemovableZuiInput(index)
        If side = GH_ParameterSide.Output Then Return IsRemovableZuiOutput(index)
        Return False
    End Function

    Public Function CreateParameter(side As GH_ParameterSide, index As Integer) As IGH_Param Implements IGH_VariableParameterComponent.CreateParameter
        If side = GH_ParameterSide.Input Then
            Dim kind As ZuiOptionalKind = KindToInsertAt(index)
            If kind = ZuiOptionalKind.None Then Return Nothing
            Return CreateZuiParam(kind)
        End If
        If side = GH_ParameterSide.Output Then
            Dim outKind As ZuiOptionalOutputKind = KindToInsertOutputAt(index)
            If outKind = ZuiOptionalOutputKind.None Then Return Nothing
            Return CreateZuiOutputParam(outKind)
        End If
        Return Nothing
    End Function

    Public Function DestroyParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.DestroyParameter
        Return True
    End Function

    Public Sub VariableParameterMaintenance() Implements IGH_VariableParameterComponent.VariableParameterMaintenance
        If Params Is Nothing Then Return
        ApplyZuiInputLayout()
    End Sub

#End Region

#Region "State"

    ''' <summary>Normalized slider parameter (0-1) per curve index (persisted in the GH file).</summary>
    Friend SliderParams As New List(Of Double)

    Public PreserveChanges As Boolean = True
    Public ProximityCache As Boolean = False
    ''' <summary>When true, viewport interaction requires the component to be selected on the canvas.</summary>
    Public LockUnselected As Boolean = True
    ''' <summary>Show/enter real curve-domain parameters instead of normalized 0-1.</summary>
    Public ShowRealValue As Boolean = True
    ''' <summary>Adds the D input; values are shown/entered/output remapped into that domain.</summary>
    Public CustomDomain As Boolean = False
    ''' <summary>Adds the S input; dragging sticks to those values, drawn as short ticks on the curve.</summary>
    Public SnapValues As Boolean = False
    ''' <summary>While dragging, quantize to dynamic ruler tick steps. Off by default.</summary>
    Public SnappingTicks As Boolean = False
    ''' <summary>Adds the Sp input; normalized curve parameter for initial slider location per curve.</summary>
    Public StartingPosition As Boolean = False

    ''' <summary>Curves from the last solve (duplicates).</summary>
    Friend Curves As New List(Of Curve)
    ''' <summary>Data tree path per curve (parallel to Curves).</summary>
    Friend CurvePaths As New List(Of GH_Path)
    ''' <summary>Evaluated slider points from the last solve (parallel to Curves).</summary>
    Friend Points As New List(Of Point3d)

    ''' <summary>Cached curves used to detect upstream changes.</summary>
    Private CacheCurves As List(Of Curve) = Nothing

    Friend Function HasValidCustomIntervalForIndex(index As Integer) As Boolean
        If Not CustomDomain Then Return False
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Dim iv As Interval = SlotSettings(index).CustomInterval
            Return SlotSettings(index).HasCustomInterval AndAlso iv.IsValid AndAlso Math.Abs(iv.Length) > Rhino.RhinoMath.ZeroTolerance
        End If
        Return False
    End Function

    Friend Function ShowRealValueForIndex(index As Integer) As Boolean
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length Then
            Return SlotSettings(index).ShowRealValue
        End If
        Return ShowRealValue
    End Function

    Private Function CustomIntervalForIndex(index As Integer) As Interval
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length AndAlso SlotSettings(index).HasCustomInterval Then
            Return SlotSettings(index).CustomInterval
        End If
        Return Interval.Unset
    End Function

    Private Function SnapDisplayValuesForIndex(index As Integer) As List(Of Double)
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length AndAlso SlotSettings(index).SnapDisplayValues IsNot Nothing Then
            Return SlotSettings(index).SnapDisplayValues
        End If
        Return SnapDisplayValues
    End Function

    Friend Function HasFixedSnapTickStepForIndex(index As Integer) As Boolean
        If Not SnappingTicks Then Return False
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length AndAlso SlotSettings(index).HasSnapTickStep Then
            Dim stepVal As Double = SlotSettings(index).SnapTickStep
            Return stepVal > 0 AndAlso Not Double.IsNaN(stepVal) AndAlso Not Double.IsInfinity(stepVal)
        End If
        Return HasFixedSnapTickStep()
    End Function

    Private Function SnapTickStepForIndex(index As Integer) As Double
        If SlotSettings IsNot Nothing AndAlso index >= 0 AndAlso index < SlotSettings.Length AndAlso SlotSettings(index).HasSnapTickStep Then
            Return SlotSettings(index).SnapTickStep
        End If
        Return SnapTickStep
    End Function

    ''' <summary>Custom domain from the D input this solve (Unset when absent/invalid) — legacy global; prefer per-index slot settings.</summary>
    Friend CustomInterval As Interval = Interval.Unset

    ''' <summary>Snap values from the S input this solve (in display units) — legacy global fallback.</summary>
    Friend SnapDisplayValues As New List(Of Double)

    ''' <summary>Fixed tick step from the T input this solve (display units); 0 = use zoom-adaptive ruler.</summary>
    Friend SnapTickStep As Double = 0

    Friend Function HasValidCustomInterval() As Boolean
        Return HasValidCustomIntervalForIndex(0)
    End Function

    ''' <summary>Display value at a normalized curve parameter (for tick labels), per current settings.</summary>
    Friend Function DisplayValueAtNormalized(index As Integer, tNorm As Double) As Double
        Dim t As Double = Math.Max(0.0R, Math.Min(1.0R, tNorm))
        If HasValidCustomIntervalForIndex(index) Then Return CustomIntervalForIndex(index).ParameterAt(t)
        If ShowRealValueForIndex(index) AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            Return Curves(index).Domain.ParameterAt(t)
        End If
        Return t
    End Function

    ''' <summary>Normalized t → displayed/output value per current settings.</summary>
    Friend Function DisplayValue(index As Integer) As Double
        Dim t As Double = If(index < SliderParams.Count, SliderParams(index), 0.5R)
        If HasValidCustomIntervalForIndex(index) Then Return CustomIntervalForIndex(index).ParameterAt(t)
        If ShowRealValueForIndex(index) AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            Return Curves(index).Domain.ParameterAt(t)
        End If
        Return t
    End Function

    ''' <summary>Displayed value → normalized t without clamping; False when the mapping is undefined.</summary>
    Friend Function TryNormalizedFromDisplayValueUnclamped(index As Integer, value As Double, ByRef t As Double) As Boolean
        If HasValidCustomIntervalForIndex(index) Then
            t = CustomIntervalForIndex(index).NormalizedParameterAt(value)
        ElseIf ShowRealValueForIndex(index) AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            t = Curves(index).Domain.NormalizedParameterAt(value)
        Else
            t = value
        End If
        Return Not Double.IsNaN(t) AndAlso Not Double.IsInfinity(t)
    End Function

    ''' <summary>Normalized snap parameters (0-1) for one curve, from the S input values in display units.</summary>
    Friend Function SnapParamsForCurve(index As Integer) As List(Of Double)
        Dim result As New List(Of Double)
        If Not SnapValues Then Return result
        Dim src As List(Of Double) = SnapDisplayValuesForIndex(index)
        If src Is Nothing OrElse src.Count = 0 Then Return result
        For Each v As Double In src
            Dim t As Double
            If Not TryNormalizedFromDisplayValueUnclamped(index, v, t) Then Continue For
            If t < -0.000001R OrElse t > 1.000001R Then Continue For
            result.Add(Math.Max(0.0R, Math.Min(1.0R, t)))
        Next
        Return result
    End Function

    ''' <summary>Starting normalized params from the Sp input this solve (parallel to Curves).</summary>
    Friend StartingParams As New List(Of Double)

    Friend SliderMouse As CurveSliderMouse
    Friend SliderTextBox As FormCurveSliderBox = Nothing
    ''' <summary>Slot index currently being edited in the floating box (-1 = none).</summary>
    Friend EditIndex As Integer = -1

    ''' <summary>Previous Cc input value for rising-edge clear-cache detection.</summary>
    Private _clearCacheInputPrev As Boolean = False

    ''' <summary>Minimum on-screen curve length before ruler ticks and numeric labels are drawn.</summary>
    Private Const RulerMinCurveScreenLengthPx As Double = 28.0R

    ''' <summary>Minimum on-screen spacing between ruler tick marks (pixels).</summary>
    Private Const RulerMinTickSpacingPx As Double = 10.0R

    ''' <summary>Minimum on-screen spacing between numeric ruler labels (pixels).</summary>
    Private Const RulerMinLabelSpacingPx As Double = 78.0R

    ''' <summary>Hard cap on ruler ticks drawn in the visible range.</summary>
    Private Const RulerMaxTickCount As Integer = 250

    ''' <summary>Screen-space radius for snap/ruler label collision (small — only near-identical values).</summary>
    Private Const SnapLabelClearancePx As Double = 28.0R

    Private Shared Function ViewportDiagonalPx(vp As RhinoViewport) As Double
        Dim w As Double = 1920.0R
        Dim h As Double = 1080.0R
        If vp IsNot Nothing Then
            Try
                Dim b As System.Drawing.Rectangle = vp.Bounds
                If b.Width > 0 AndAlso b.Height > 0 Then
                    w = CDbl(b.Width)
                    h = CDbl(b.Height)
                End If
            Catch
            End Try
        End If
        Return Math.Sqrt(w * w + h * h)
    End Function

    Private Shared Function IsNearViewport(sp As Rhino.Geometry.Point2d, bounds As System.Drawing.Rectangle, marginPx As Double) As Boolean
        Return sp.X >= bounds.Left - marginPx AndAlso sp.X <= bounds.Right + marginPx AndAlso
               sp.Y >= bounds.Top - marginPx AndAlso sp.Y <= bounds.Bottom + marginPx
    End Function

    Private Shared Function ViewportVisibilityMarginPx(vp As RhinoViewport) As Double
        Return Math.Max(80.0R, ViewportDiagonalPx(vp) * 0.18R)
    End Function

    ''' <summary>True when the curve point at normalized t is on or near the viewport.</summary>
    Private Shared Function IsCurvePointNearViewport(vp As RhinoViewport, crv As Curve, tNorm As Double) As Boolean
        If vp Is Nothing OrElse crv Is Nothing Then Return False
        Dim pt As Point3d = crv.PointAt(crv.Domain.ParameterAt(Math.Max(0.0R, Math.Min(1.0R, tNorm))))
        If Not pt.IsValid Then Return False
        Dim sp As Rhino.Geometry.Point2d = vp.WorldToClient(pt)
        Return IsNearViewport(sp, vp.Bounds, ViewportVisibilityMarginPx(vp))
    End Function

    ''' <summary>Normalized parameter interval of the curve currently visible in the viewport.</summary>
    Private Shared Function TryVisibleNormalizedRange(crv As Curve, vp As RhinoViewport, ByRef tLo As Double, ByRef tHi As Double) As Boolean
        tLo = 0
        tHi = 1
        If crv Is Nothing OrElse vp Is Nothing Then Return False

        Const samples As Integer = 256
        Dim bounds As System.Drawing.Rectangle = vp.Bounds
        Dim margin As Double = ViewportVisibilityMarginPx(vp)
        Dim found As Boolean = False

        For i As Integer = 0 To samples
            Dim t As Double = CDbl(i) / CDbl(samples)
            Dim wp As Point3d = crv.PointAt(crv.Domain.ParameterAt(t))
            If Not wp.IsValid Then Continue For
            Dim sp As Rhino.Geometry.Point2d = vp.WorldToClient(wp)
            If Not IsNearViewport(sp, bounds, margin) Then Continue For
            If Not found Then
                tLo = t
                tHi = t
                found = True
            Else
                tLo = Math.Min(tLo, t)
                tHi = Math.Max(tHi, t)
            End If
        Next

        If Not found Then Return False

        ' Pad generously so edge ticks are not culled when the estimate is slightly tight.
        Dim padT As Double = Math.Max(0.04R, (tHi - tLo) * 0.22R + 2.0R / CDbl(samples))
        tLo = Math.Max(0.0R, tLo - padT)
        tHi = Math.Min(1.0R, tHi + padT)
        Return True
    End Function

    ''' <summary>Display-value step from local on-screen scale at t — keeps refining as the user zooms in.</summary>
    Private Function ComputeRawRulerStepAt(index As Integer, crv As Curve, vp As RhinoViewport, tCenter As Double) As Double
        Const dtNorm As Double = 0.002R
        Dim tA As Double = Math.Max(0.0R, tCenter - dtNorm)
        Dim tB As Double = Math.Min(1.0R, tCenter + dtNorm)
        If tB - tA < 0.00001R Then Return 0

        Dim vA As Double = DisplayValueAtNormalized(index, tA)
        Dim vB As Double = DisplayValueAtNormalized(index, tB)
        Dim vSpan As Double = Math.Abs(vB - vA)
        If vSpan <= 0 Then Return 0

        Dim pA As Rhino.Geometry.Point2d = vp.WorldToClient(crv.PointAt(crv.Domain.ParameterAt(tA)))
        Dim pB As Rhino.Geometry.Point2d = vp.WorldToClient(crv.PointAt(crv.Domain.ParameterAt(tB)))
        Dim dx As Double = pB.X - pA.X
        Dim dy As Double = pB.Y - pA.Y
        Dim pxDist As Double = Math.Sqrt(dx * dx + dy * dy)
        If pxDist < 0.5R Then Return 0

        Return vSpan / pxDist * RulerMinTickSpacingPx
    End Function

    ''' <summary>On-screen curve length from viewport samples; uses min(sampled, arc estimate) so zoom-out is not overestimated.</summary>
    Private Shared Function MeasureCurveScreenLengthPx(crv As Curve, vp As RhinoViewport) As Double
        If crv Is Nothing OrElse vp Is Nothing Then Return 0

        Const samples As Integer = 48
        Dim diag As Double = ViewportDiagonalPx(vp)
        Dim margin As Double = diag * 0.25R
        Dim maxSeg As Double = diag * 0.5R
        Dim bounds As System.Drawing.Rectangle = vp.Bounds

        Dim screenLen As Double = 0
        Dim prevSp As New Rhino.Geometry.Point2d
        Dim hasPrev As Boolean = False
        Dim prevVis As Boolean = False

        For i As Integer = 0 To samples
            Dim wp As Point3d = crv.PointAt(crv.Domain.ParameterAt(CDbl(i) / CDbl(samples)))
            If Not wp.IsValid Then Continue For
            Dim sp As Rhino.Geometry.Point2d = vp.WorldToClient(wp)
            Dim vis As Boolean = IsNearViewport(sp, bounds, margin)

            If hasPrev AndAlso (vis OrElse prevVis) Then
                Dim dx As Double = sp.X - prevSp.X
                Dim dy As Double = sp.Y - prevSp.Y
                Dim seg As Double = Math.Sqrt(dx * dx + dy * dy)
                If seg > maxSeg Then seg = maxSeg
                screenLen += seg
            End If

            prevSp = sp
            prevVis = vis
            hasPrev = True
        Next

        Dim arcEstimate As Double = 0
        Try
            Dim mid As Point3d = crv.PointAt(crv.Domain.Mid)
            Dim pxPerUnit As Double = 0
            vp.GetWorldToScreenScale(mid, pxPerUnit)
            If pxPerUnit > 0 Then
                Dim arcLen As Double = crv.GetLength()
                If arcLen > 0 Then arcEstimate = arcLen * pxPerUnit
            End If
        Catch
        End Try

        If screenLen <= 0 Then Return arcEstimate
        If arcEstimate <= 0 Then Return screenLen
        Return Math.Min(screenLen, arcEstimate)
    End Function

    Private Shared Function ShouldShowRulerAnnotations(crv As Curve, vp As RhinoViewport) As Boolean
        Return MeasureCurveScreenLengthPx(crv, vp) >= RulerMinCurveScreenLengthPx
    End Function

    ''' <summary>On-screen curve length using only viewport-near samples (off-screen points are ignored).</summary>
    Private Shared Function EstimateVisibleCurveScreenLength(crv As Curve, vp As RhinoViewport) As Double
        Dim screenLen As Double = MeasureCurveScreenLengthPx(crv, vp)
        If screenLen <= 0 Then Return 0

        ' When partly on-screen, allow a modest boost for step sizing (not for show/hide).
        If screenLen < 60.0R Then
            Try
                Dim mid As Point3d = crv.PointAt(crv.Domain.Mid)
                Dim pxPerUnit As Double = 0
                vp.GetWorldToScreenScale(mid, pxPerUnit)
                If pxPerUnit > 0 Then
                    Dim arcLen As Double = crv.GetLength()
                    If arcLen > 0 Then screenLen = Math.Max(screenLen, arcLen * pxPerUnit)
                End If
            Catch
            End Try
        End If

        Dim cap As Double = ViewportDiagonalPx(vp) * 1.5R
        If screenLen > cap Then screenLen = cap
        Return screenLen
    End Function

    ''' <summary>Round a step size up to the next 1/2/5 × 10^k value.</summary>
    Private Shared Function SnapRulerStepUp(rawStep As Double) As Double
        If rawStep <= 0 OrElse Double.IsNaN(rawStep) OrElse Double.IsInfinity(rawStep) Then Return 0
        Dim log10 As Double = Math.Log10(rawStep)
        If Double.IsNaN(log10) OrElse Double.IsInfinity(log10) Then Return rawStep
        Dim decade As Double = Math.Pow(10.0R, Math.Floor(log10))
        If decade <= 0 OrElse Double.IsNaN(decade) OrElse Double.IsInfinity(decade) Then Return rawStep
        Dim mant As Double = rawStep / decade
        If mant <= 1.0R Then Return decade
        If mant <= 2.0R Then Return 2.0R * decade
        If mant <= 5.0R Then Return 5.0R * decade
        Return 10.0R * decade
    End Function

    Private Shared Sub ApplyRulerMajorEvery(rawStep As Double, ByRef stepVal As Double, ByRef majorEvery As Integer)
        Dim log10 As Double = Math.Log10(rawStep)
        If Double.IsNaN(log10) OrElse Double.IsInfinity(log10) Then
            majorEvery = 10
            Return
        End If
        Dim decade As Double = Math.Pow(10.0R, Math.Floor(log10))
        If decade <= 0 Then
            majorEvery = 10
            Return
        End If
        Dim mant As Double = rawStep / decade
        If mant <= 1.0R Then
            stepVal = decade
            majorEvery = 10
        ElseIf mant <= 2.0R Then
            stepVal = 2.0R * decade
            majorEvery = 5
        ElseIf mant <= 5.0R Then
            stepVal = 5.0R * decade
            majorEvery = 2
        Else
            stepVal = 10.0R * decade
            majorEvery = 10
        End If
    End Sub

    ''' <summary>
    ''' Ruler layout for one curve in one viewport: minor step in display units (1/2/5 × 10^k series)
    ''' and how many minors per labeled major. Denser as the curve takes more screen space.
    ''' </summary>
    Friend Function TryComputeRulerStep(index As Integer, vp As RhinoViewport, ByRef stepVal As Double, ByRef majorEvery As Integer) As Boolean
        stepVal = 0
        majorEvery = 10
        If vp Is Nothing OrElse index < 0 OrElse index >= Curves.Count Then Return False
        Dim crv As Curve = Curves(index)
        If crv Is Nothing Then Return False
        If Not ShouldShowRulerAnnotations(crv, vp) Then Return False

        Dim tVisLo As Double, tVisHi As Double
        If Not TryVisibleNormalizedRange(crv, vp, tVisLo, tVisHi) Then Return False

        Dim dVis0 As Double = DisplayValueAtNormalized(index, tVisLo)
        Dim dVis1 As Double = DisplayValueAtNormalized(index, tVisHi)
        Dim visSpan As Double = Math.Abs(dVis1 - dVis0)

        Dim d0 As Double = DisplayValueAtNormalized(index, 0.0R)
        Dim d1 As Double = DisplayValueAtNormalized(index, 1.0R)
        Dim span As Double = Math.Abs(d1 - d0)
        If span < Rhino.RhinoMath.ZeroTolerance Then Return False
        If visSpan < Rhino.RhinoMath.ZeroTolerance Then visSpan = span

        Dim tCenter As Double = (tVisLo + tVisHi) * 0.5R
        Dim rawStep As Double = ComputeRawRulerStepAt(index, crv, vp, tCenter)
        If rawStep <= 0 OrElse Double.IsNaN(rawStep) OrElse Double.IsInfinity(rawStep) Then
            ' Fallback when local scale is degenerate.
            Dim screenLen As Double = EstimateVisibleCurveScreenLength(crv, vp)
            If screenLen < RulerMinCurveScreenLengthPx Then Return False
            rawStep = visSpan * RulerMinTickSpacingPx / screenLen
        End If
        If rawStep <= 0 OrElse Double.IsNaN(rawStep) OrElse Double.IsInfinity(rawStep) Then Return False

        ApplyRulerMajorEvery(rawStep, stepVal, majorEvery)

        ' Cap tick density in the visible range only (not the full curve domain).
        Dim minStep As Double = visSpan / CDbl(RulerMaxTickCount)
        If stepVal < minStep Then
            stepVal = SnapRulerStepUp(minStep)
            ApplyRulerMajorEvery(stepVal, stepVal, majorEvery)
        End If

        Return stepVal > 0
    End Function

    Friend Function HasFixedSnapTickStep() As Boolean
        Return SnappingTicks AndAlso SnapTickStep > 0 AndAlso Not Double.IsNaN(SnapTickStep) AndAlso Not Double.IsInfinity(SnapTickStep)
    End Function

    ''' <summary>Quantize display value to the nearest multiple of step anchored at zero.</summary>
    Private Shared Function QuantizeDisplayValueToStep(v As Double, stepVal As Double) As Double
        Return Math.Round(v / stepVal) * stepVal
    End Function

    ''' <summary>Quantize a normalized drag parameter to tick steps — fixed T input or zoom-adaptive ruler.</summary>
    Friend Function QuantizeToRulerStep(index As Integer, tNorm As Double, view As Rhino.Display.RhinoView) As Double
        If Not SnappingTicks Then Return tNorm

        Dim stepVal As Double = 0
        If HasFixedSnapTickStepForIndex(index) Then
            stepVal = SnapTickStepForIndex(index)
        ElseIf view IsNot Nothing Then
            Dim majorEvery As Integer
            If Not TryComputeRulerStep(index, view.ActiveViewport, stepVal, majorEvery) Then Return tNorm
        Else
            Return tNorm
        End If

        If stepVal <= 0 Then Return tNorm

        Dim v As Double = DisplayValueAtNormalized(index, tNorm)
        v = QuantizeDisplayValueToStep(v, stepVal)
        Dim t As Double
        If Not TryNormalizedFromDisplayValueUnclamped(index, v, t) Then Return tNorm
        If Double.IsNaN(t) Then Return tNorm
        Return Math.Max(0.0R, Math.Min(1.0R, t))
    End Function

    Friend Function InitialSliderParam(index As Integer) As Double
        If StartingParams IsNot Nothing AndAlso index >= 0 AndAlso index < StartingParams.Count Then
            Return StartingParams(index)
        End If
        Return 0.5R
    End Function

    ''' <summary>Displayed value → normalized t per current settings (clamped 0-1).</summary>
    Friend Function NormalizedFromDisplayValue(index As Integer, value As Double) As Double
        Dim t As Double
        If HasValidCustomIntervalForIndex(index) Then
            t = CustomIntervalForIndex(index).NormalizedParameterAt(value)
        ElseIf ShowRealValueForIndex(index) AndAlso index < Curves.Count AndAlso Curves(index) IsNot Nothing Then
            t = Curves(index).Domain.NormalizedParameterAt(value)
        Else
            t = value
        End If
        If Double.IsNaN(t) Then t = 0.5R
        Return Math.Max(0.0R, Math.Min(1.0R, t))
    End Function

    Friend Sub SetStateFromUndo(newParams As List(Of Double), newPreserve As Boolean, newProximity As Boolean,
                                newReal As Boolean, newCustomDomain As Boolean, newSnapValues As Boolean, newSnappingTicks As Boolean,
                                newStartingPosition As Boolean, newLockUnselected As Boolean)
        SliderParams = New List(Of Double)(newParams)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        ShowRealValue = newReal
        Dim needSync As Boolean = (CustomDomain <> newCustomDomain) OrElse (SnapValues <> newSnapValues) OrElse (SnappingTicks <> newSnappingTicks) OrElse (StartingPosition <> newStartingPosition)
        CustomDomain = newCustomDomain
        SnapValues = newSnapValues
        SnappingTicks = newSnappingTicks
        StartingPosition = newStartingPosition
        LockUnselected = newLockUnselected
        If needSync Then SyncOptionalInputs()
        CloseSliderTextBoxIfAny()
        SyncMouse()
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
            SliderParams.Add(InitialSliderParam(SliderParams.Count))
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
            SliderParams.Add(InitialSliderParam(SliderParams.Count))
        End While
        SliderParams(index) = Math.Max(0.0R, Math.Min(1.0R, t))
        Me.ExpireSolution(True)
    End Sub

    Private Sub ShutDownInteraction()
        CloseSliderTextBoxIfAny()
        If SliderMouse IsNot Nothing Then SliderMouse.Enabled = False
    End Sub

    ''' <summary>Viewport interaction is live when unlocked, previewed, has curves, and selection rules allow it.</summary>
    Friend Sub SyncMouse()
        Dim selectionOk As Boolean = IsSelectionAllowedForViewport()
        Dim want As Boolean =
            selectionOk AndAlso
            Not Me.Locked AndAlso
            Not Me.Hidden AndAlso
            Curves.Count > 0
        If SliderMouse IsNot Nothing Then
            If SliderMouse.Enabled <> want Then SliderMouse.Enabled = want
        End If
        If Not want Then CloseSliderTextBoxIfAny()
    End Sub

#End Region

#Region "Per-curve slot settings (tree-matched optional inputs)"

    Private Sub MapBoolTreeToCurveSlots(DA As IGH_DataAccess, nick As String, curveData As GH_Structure(Of GH_Curve),
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

        Dim flat As Integer = 0
        For Each path As GH_Path In curveData.Paths
            Dim curveBranch As IList(Of GH_Curve) = curveData.DataList(path)
            Dim valueBranch As IList(Of GH_Boolean) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To curveBranch.Count - 1
                If flat < SlotSettings.Length Then
                    Dim v As Boolean = defaultValue
                    If useBroadcast Then
                        v = broadcast
                    ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        v = valueBranch(j).Value
                    End If
                    apply(flat, v)
                End If
                flat += 1
            Next
        Next
    End Sub

    Private Sub MapIntervalTreeToCurveSlots(DA As IGH_DataAccess, nick As String, curveData As GH_Structure(Of GH_Curve),
                                            apply As Action(Of Integer, Interval))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindInputIndexByNick(nick)
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Interval)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcast As Interval = Interval.Unset
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gi As GH_Interval = tree.AllData(True).FirstOrDefault()
            If gi IsNot Nothing Then broadcast = gi.Value
        End If

        Dim flat As Integer = 0
        For Each path As GH_Path In curveData.Paths
            Dim curveBranch As IList(Of GH_Curve) = curveData.DataList(path)
            Dim valueBranch As IList(Of GH_Interval) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To curveBranch.Count - 1
                If flat < SlotSettings.Length Then
                    Dim iv As Interval = Interval.Unset
                    If useBroadcast Then
                        iv = broadcast
                    ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing Then
                        iv = valueBranch(j).Value
                    End If
                    If iv.IsValid Then apply(flat, iv)
                End If
                flat += 1
            Next
        Next
    End Sub

    Private Sub MapNumberTreeToCurveSlots(DA As IGH_DataAccess, nick As String, curveData As GH_Structure(Of GH_Curve),
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

        Dim flat As Integer = 0
        For Each path As GH_Path In curveData.Paths
            Dim curveBranch As IList(Of GH_Curve) = curveData.DataList(path)
            Dim valueBranch As IList(Of GH_Number) = Nothing
            If Not useBroadcast AndAlso tree.PathExists(path) Then valueBranch = tree.Branch(path)
            For j As Integer = 0 To curveBranch.Count - 1
                If flat < SlotSettings.Length Then
                    Dim v As Double = 0
                    Dim hasV As Boolean = False
                    If useBroadcast Then
                        v = broadcast
                        hasV = True
                    ElseIf valueBranch IsNot Nothing AndAlso j < valueBranch.Count AndAlso valueBranch(j) IsNot Nothing AndAlso valueBranch(j).IsValid Then
                        v = valueBranch(j).Value
                        hasV = True
                    End If
                    If hasV Then apply(flat, v)
                End If
                flat += 1
            Next
        Next
    End Sub

    Private Sub MapSnapValuesTreeToCurveSlots(DA As IGH_DataAccess, curveData As GH_Structure(Of GH_Curve))
        If SlotSettings Is Nothing Then Return
        Dim ix As Integer = FindSnapInputIndex()
        If ix < 0 OrElse Params.Input(ix).SourceCount = 0 Then Return
        Dim tree As New GH_Structure(Of GH_Number)
        If Not DA.GetDataTree(ix, tree) Then Return

        Dim broadcastVals As New List(Of Double)
        Dim useBroadcast As Boolean = False
        If tree.DataCount = 1 Then
            useBroadcast = True
            Dim gn As GH_Number = tree.AllData(True).FirstOrDefault()
            If gn IsNot Nothing AndAlso gn.IsValid AndAlso Not Double.IsNaN(gn.Value) AndAlso Not Double.IsInfinity(gn.Value) Then
                broadcastVals.Add(gn.Value)
            End If
        End If

        Dim pathSnaps As New Dictionary(Of GH_Path, List(Of Double))
        If Not useBroadcast Then
            For Each path As GH_Path In tree.Paths
                Dim vals As New List(Of Double)
                For Each gn As GH_Number In tree.Branch(path)
                    If gn IsNot Nothing AndAlso gn.IsValid AndAlso Not Double.IsNaN(gn.Value) AndAlso Not Double.IsInfinity(gn.Value) Then
                        vals.Add(gn.Value)
                    End If
                Next
                If vals.Count > 0 Then pathSnaps(path) = vals
            Next
        End If

        Dim flat As Integer = 0
        For Each path As GH_Path In curveData.Paths
            Dim curveBranch As IList(Of GH_Curve) = curveData.DataList(path)
            Dim vals As List(Of Double) = Nothing
            If useBroadcast Then
                vals = broadcastVals
            ElseIf pathSnaps.ContainsKey(path) Then
                vals = pathSnaps(path)
            End If
            For j As Integer = 0 To curveBranch.Count - 1
                If flat < SlotSettings.Length AndAlso vals IsNot Nothing AndAlso vals.Count > 0 Then
                    SlotSettings(flat).SnapDisplayValues = New List(Of Double)(vals)
                End If
                flat += 1
            Next
        Next
    End Sub

    Private Sub BuildSlotSettings(DA As IGH_DataAccess, curveData As GH_Structure(Of GH_Curve))
        Dim n As Integer = Curves.Count
        If n <= 0 Then
            SlotSettings = Nothing
            Return
        End If

        ReDim SlotSettings(n - 1)
        For i As Integer = 0 To n - 1
            Dim s As CurveSliderSlotSettings
            s.Active = True
            s.ShowRealValue = ShowRealValue
            s.CustomInterval = Interval.Unset
            s.HasCustomInterval = False
            s.SnapDisplayValues = New List(Of Double)
            s.SnapTickStep = 0
            s.HasSnapTickStep = False
            SlotSettings(i) = s
        Next

        If HasZuiInput(ZuiOptionalKind.Active) Then
            MapBoolTreeToCurveSlots(DA, "Ac", curveData, True, Sub(i, v) SlotSettings(i).Active = v)
        End If
        If HasZuiInput(ZuiOptionalKind.RealValues) Then
            MapBoolTreeToCurveSlots(DA, "Rv", curveData, ShowRealValue, Sub(i, v) SlotSettings(i).ShowRealValue = v)
        End If
        If HasZuiInput(ZuiOptionalKind.CustomDomain) Then
            MapIntervalTreeToCurveSlots(DA, "D", curveData,
                Sub(i, iv)
                    SlotSettings(i).CustomInterval = iv
                    SlotSettings(i).HasCustomInterval = iv.IsValid AndAlso Math.Abs(iv.Length) > Rhino.RhinoMath.ZeroTolerance
                End Sub)
        End If
        If HasZuiInput(ZuiOptionalKind.SnapValues) Then
            MapSnapValuesTreeToCurveSlots(DA, curveData)
        End If
        If HasZuiInput(ZuiOptionalKind.SnappingTicks) Then
            MapNumberTreeToCurveSlots(DA, "T", curveData,
                Sub(i, v)
                    If v > 0 AndAlso Not Double.IsNaN(v) AndAlso Not Double.IsInfinity(v) Then
                        SlotSettings(i).SnapTickStep = v
                        SlotSettings(i).HasSnapTickStep = True
                    End If
                End Sub)
        End If
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

    Private Shared Function ClampNormalizedParam(v As Double) As Double
        If Double.IsNaN(v) OrElse Double.IsInfinity(v) Then Return 0.5R
        Return Math.Max(0.0R, Math.Min(1.0R, v))
    End Function

    ''' <summary>Convert a Sp input value into normalized slider storage per current display settings.</summary>
    Private Function StartingInputToNormalized(index As Integer, crv As Curve, value As Double) As Double
        If HasValidCustomIntervalForIndex(index) Then
            Return ClampNormalizedParam(CustomIntervalForIndex(index).NormalizedParameterAt(value))
        ElseIf ShowRealValueForIndex(index) AndAlso crv IsNot Nothing Then
            Return ClampNormalizedParam(crv.Domain.NormalizedParameterAt(value))
        Else
            Return ClampNormalizedParam(value)
        End If
    End Function

    Private Sub BuildStartingParamsFromTree(curveData As GH_Structure(Of GH_Curve), startData As GH_Structure(Of GH_Number),
                                          newCurves As List(Of Curve), result As List(Of Double))
        result.Clear()
        For i As Integer = 0 To newCurves.Count - 1
            result.Add(0.5R)
        Next
        If startData Is Nothing OrElse startData.DataCount = 0 Then Return

        Dim broadcast As Double = 0.5R
        Dim broadcastSet As Boolean = False
        If startData.DataCount = 1 Then
            Dim gn As GH_Number = startData.AllData(True).FirstOrDefault()
            If gn IsNot Nothing Then
                broadcast = gn.Value
                broadcastSet = True
            End If
        End If

        Dim flat As Integer = 0
        For Each path As GH_Path In curveData.Paths
            Dim curveBranch As IList(Of GH_Curve) = curveData.DataList(path)
            Dim startBranch As IList(Of GH_Number) = Nothing
            If startData.PathExists(path) Then startBranch = startData.DataList(path)
            For j As Integer = 0 To curveBranch.Count - 1
                If flat < result.Count Then
                    Dim crv As Curve = If(flat < newCurves.Count, newCurves(flat), Nothing)
                    Dim t As Double = 0.5R
                    If startBranch IsNot Nothing AndAlso j < startBranch.Count AndAlso startBranch(j) IsNot Nothing Then
                        t = StartingInputToNormalized(flat, crv, startBranch(j).Value)
                    ElseIf broadcastSet Then
                        t = StartingInputToNormalized(flat, crv, broadcast)
                    End If
                    result(flat) = t
                End If
                flat += 1
            Next
        Next
    End Sub

    ''' <summary>Greedy nearest matching: each old slider point claims the closest new curve; its param becomes the projection of the old point onto that curve.</summary>
    Private Shared Function RemapParamsByProximity(oldCurves As List(Of Curve), oldParams As List(Of Double), newCurves As List(Of Curve),
                                                   startingParams As List(Of Double)) As List(Of Double)
        Dim result As New List(Of Double)(newCurves.Count)
        For i As Integer = 0 To newCurves.Count - 1
            Dim t0 As Double = 0.5R
            If startingParams IsNot Nothing AndAlso i < startingParams.Count Then t0 = startingParams(i)
            result.Add(t0)
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

    Private Shared Sub BuildCurvesFromTree(curveData As GH_Structure(Of GH_Curve), newCurves As List(Of Curve), newPaths As List(Of GH_Path))
        newCurves.Clear()
        newPaths.Clear()
        For Each path As GH_Path In curveData.Paths
            For Each gc As GH_Curve In curveData.DataList(path)
                If gc Is Nothing OrElse gc.Value Is Nothing Then
                    newCurves.Add(Nothing)
                Else
                    newCurves.Add(gc.Value.DuplicateCurve())
                End If
                newPaths.Add(path)
            Next
        Next
    End Sub

    Private Sub SetOutputTrees(DA As IGH_DataAccess)
        Dim outPts As New GH_Structure(Of GH_Point)
        Dim outVals As New GH_Structure(Of GH_Number)
        For i As Integer = 0 To Curves.Count - 1
            Dim path As GH_Path = If(i < CurvePaths.Count, CurvePaths(i), New GH_Path(0))
            Dim pt As Point3d = If(i < Points.Count, Points(i), Point3d.Unset)
            outPts.Append(New GH_Point(pt), path)
            outVals.Append(New GH_Number(DisplayValue(i)), path)
        Next
        DA.SetDataTree(0, outPts)
        DA.SetDataTree(1, outVals)

        Dim uIx As Integer = FindOutputIndexByNick("u")
        If uIx >= 0 Then
            Dim outU As New GH_Structure(Of GH_Number)
            For i As Integer = 0 To Curves.Count - 1
                Dim path As GH_Path = If(i < CurvePaths.Count, CurvePaths(i), New GH_Path(0))
                Dim u As Double = If(i < SliderParams.Count, SliderParams(i), 0.5R)
                outU.Append(New GH_Number(u), path)
            Next
            DA.SetDataTree(uIx, outU)
        End If

        Dim domIx As Integer = FindOutputIndexByNick("Dom")
        If domIx >= 0 Then
            Dim outDom As New GH_Structure(Of GH_Interval)
            For i As Integer = 0 To Curves.Count - 1
                Dim path As GH_Path = If(i < CurvePaths.Count, CurvePaths(i), New GH_Path(0))
                Dim dom As Interval = Interval.Unset
                If i < Curves.Count AndAlso Curves(i) IsNot Nothing Then dom = Curves(i).Domain
                outDom.Append(New GH_Interval(dom), path)
            Next
            DA.SetDataTree(domIx, outDom)
        End If
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim curveData As New GH_Structure(Of GH_Curve)
        If Not DA.GetDataTree(0, curveData) Then
            Curves.Clear()
            CurvePaths.Clear()
            Points.Clear()
            CacheCurves = Nothing
            SyncMouse()
            Exit Sub
        End If

        CustomInterval = Interval.Unset
        SnapDisplayValues.Clear()
        SnapTickStep = 0

        SyncFeatureFlagsFromInputs()
        ApplyZuiBooleanInputs(DA)
        ApplyRealValuesFromInput(DA)

        Dim newCurves As New List(Of Curve)
        Dim newPaths As New List(Of GH_Path)
        BuildCurvesFromTree(curveData, newCurves, newPaths)

        Curves = newCurves
        CurvePaths = newPaths
        BuildSlotSettings(DA, curveData)

        StartingParams.Clear()
        If StartingPosition Then
            Dim spIx As Integer = FindStartingPositionInputIndex()
            If spIx >= 0 AndAlso Me.Params.Input(spIx).SourceCount > 0 Then
                Dim startData As New GH_Structure(Of GH_Number)
                If DA.GetDataTree(spIx, startData) Then
                    BuildStartingParamsFromTree(curveData, startData, Curves, StartingParams)
                End If
            End If
        End If
        While StartingParams.Count < Curves.Count
            StartingParams.Add(0.5R)
        End While

        If CacheCurves Is Nothing Then
            CacheCurves = newCurves
        ElseIf Not CurvesEqual(CacheCurves, newCurves) Then
            If ProximityCache Then
                SliderParams = RemapParamsByProximity(CacheCurves, SliderParams, newCurves, StartingParams)
            ElseIf Not PreserveChanges Then
                SliderParams.Clear()
            End If
            CacheCurves = newCurves
        End If

        While SliderParams.Count < Curves.Count
            SliderParams.Add(InitialSliderParam(SliderParams.Count))
        End While
        If Not PreserveChanges AndAlso SliderParams.Count > Curves.Count Then
            SliderParams.RemoveRange(Curves.Count, SliderParams.Count - Curves.Count)
        End If

        If EditIndex >= Curves.Count OrElse (EditIndex >= 0 AndAlso Not IsCurveActiveForViewport(EditIndex)) Then CloseSliderTextBoxIfAny()

        Points.Clear()
        For i As Integer = 0 To Curves.Count - 1
            Points.Add(PointOnCurveAtNormalized(Curves(i), SliderParams(i)))
        Next

        SetOutputTrees(DA)
        SyncMouse()
    End Sub

#End Region

#Region "Preview"

    Private Enum CurveTickKind
        Minor
        Major
        EndCap
        SnapValue
    End Enum

    ''' <summary>Curve tangent (tx,ty) in screen pixels at normalized t.</summary>
    Private Shared Function TryCurveScreenTangent(args As IGH_PreviewArgs, crv As Curve, tNorm As Double, ByRef tx As Double, ByRef ty As Double) As Boolean
        If args Is Nothing OrElse crv Is Nothing Then Return False
        Const eps As Double = 0.004R
        Dim tLo As Double = Math.Max(0.0R, tNorm - eps)
        Dim tHi As Double = Math.Min(1.0R, tNorm + eps)
        If tHi <= tLo Then Return False

        Dim pLo As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(crv.PointAt(crv.Domain.ParameterAt(tLo)))
        Dim pHi As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(crv.PointAt(crv.Domain.ParameterAt(tHi)))
        tx = pHi.X - pLo.X
        ty = pHi.Y - pLo.Y
        Dim len As Double = Math.Sqrt(tx * tx + ty * ty)
        If len < 0.5R Then
            tx = 1.0R
            ty = 0.0R
            Return True
        End If
        tx /= len
        ty /= len
        Return True
    End Function

    Private Shared Sub Draw2dLinePx(args As IGH_PreviewArgs, x0 As Double, y0 As Double, x1 As Double, y1 As Double, col As Color, thickness As Single)
        args.Display.Draw2dLine(New PointF(CSng(x0), CSng(y0)), New PointF(CSng(x1), CSng(y1)), col, thickness)
    End Sub

    ''' <summary>Draw a tick in screen pixels so size stays constant regardless of zoom.</summary>
    Private Shared Sub DrawCurveTick(args As IGH_PreviewArgs, crv As Curve, tNorm As Double, kind As CurveTickKind, col As Color,
                                     Optional tickLabel As String = Nothing,
                                     Optional placedLabels As List(Of Rhino.Geometry.Point2d) = Nothing)
        Dim tClamped As Double = Math.Max(0.0R, Math.Min(1.0R, tNorm))
        Dim pt As Point3d = crv.PointAt(crv.Domain.ParameterAt(tClamped))
        If Not pt.IsValid Then Return

        Dim tx As Double, ty As Double
        If Not TryCurveScreenTangent(args, crv, tClamped, tx, ty) Then Return
        Dim nx As Double = -ty
        Dim ny As Double = tx

        Dim sp As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(pt)
        Dim halfLenPx As Single
        Dim thickness As Single
        Select Case kind
            Case CurveTickKind.Minor
                halfLenPx = 4.0F
                thickness = 1.0F
            Case CurveTickKind.Major
                halfLenPx = 7.0F
                thickness = 1.5F
            Case CurveTickKind.EndCap
                halfLenPx = 9.0F
                thickness = 2.0F
            Case CurveTickKind.SnapValue
                halfLenPx = 8.0F
                thickness = 2.0F
            Case Else
                halfLenPx = 4.0F
                thickness = 1.0F
        End Select

        Draw2dLinePx(args, sp.X - nx * halfLenPx, sp.Y - ny * halfLenPx, sp.X + nx * halfLenPx, sp.Y + ny * halfLenPx, col, thickness)

        If kind = CurveTickKind.SnapValue Then
            Const dotHalf As Integer = 3
            Dim dotRect As New Rectangle(CInt(Math.Round(sp.X)) - dotHalf, CInt(Math.Round(sp.Y)) - dotHalf, dotHalf * 2, dotHalf * 2)
            args.Display.Draw2dRectangle(dotRect, col, 0, col)
            Dim capHalf As Single = 3.5F
            Dim tipX As Double = sp.X + nx * halfLenPx
            Dim tipY As Double = sp.Y + ny * halfLenPx
            Draw2dLinePx(args, tipX - tx * capHalf, tipY - ty * capHalf, tipX + tx * capHalf, tipY + ty * capHalf, col, thickness)
        End If

        If tickLabel Is Nothing Then Return

        Dim anchor As Rhino.Geometry.Point2d
        If kind = CurveTickKind.SnapValue Then
            anchor = SnapLabelAnchor(sp, nx, ny, tx, ty, halfLenPx)
        Else
            anchor = RulerLabelAnchor(sp, nx, ny, tx, ty, halfLenPx)
        End If

        If placedLabels IsNot Nothing AndAlso kind <> CurveTickKind.SnapValue AndAlso Not IsLabelSpacingOk(anchor, placedLabels) Then Return

        args.Display.Draw2dText(tickLabel, col, anchor, True, 10)
        If placedLabels IsNot Nothing Then placedLabels.Add(anchor)
    End Sub

    Private Shared Function IsLabelSpacingOk(anchor As Rhino.Geometry.Point2d, placed As List(Of Rhino.Geometry.Point2d)) As Boolean
        If placed Is Nothing OrElse placed.Count = 0 Then Return True
        Dim minDist2 As Double = RulerMinLabelSpacingPx * RulerMinLabelSpacingPx
        For Each p As Rhino.Geometry.Point2d In placed
            Dim dx As Double = anchor.X - p.X
            Dim dy As Double = anchor.Y - p.Y
            If dx * dx + dy * dy < minDist2 Then Return False
        Next
        Return True
    End Function

    Private Shared Function RulerLabelAnchor(sp As Rhino.Geometry.Point2d, nx As Double, ny As Double, tx As Double, ty As Double, halfLenPx As Single) As Rhino.Geometry.Point2d
        Return New Rhino.Geometry.Point2d(sp.X + nx * (halfLenPx + 12.0R) + tx * 5.0R,
                                          sp.Y + ny * (halfLenPx + 12.0R) + ty * 5.0R)
    End Function

    Private Shared Function SnapLabelAnchor(sp As Rhino.Geometry.Point2d, nx As Double, ny As Double, tx As Double, ty As Double, halfLenPx As Single) As Rhino.Geometry.Point2d
        Return New Rhino.Geometry.Point2d(sp.X + nx * (halfLenPx + 14.0R) + tx * 6.0R,
                                          sp.Y + ny * (halfLenPx + 14.0R) + ty * 6.0R)
    End Function

    Private Shared Function DecimalsForStep(stepVal As Double) As Integer
        If stepVal <= 0 OrElse Double.IsNaN(stepVal) OrElse Double.IsInfinity(stepVal) Then Return 3
        If stepVal >= 1.0R Then Return 0
        Return Math.Min(12, Math.Max(0, CInt(Math.Ceiling(-Math.Log10(stepVal) - 0.000001R))))
    End Function

    Private Shared Function FormatDisplayValue(v As Double, displayStep As Double) As String
        Return v.ToString("F" & DecimalsForStep(displayStep).ToString(), Globalization.CultureInfo.InvariantCulture)
    End Function

    ''' <summary>Decimals for labels at the current zoom level (follows ruler minor step).</summary>
    Private Function TryGetViewportLabelStep(index As Integer, vp As RhinoViewport, ByRef minorStep As Double, ByRef majorLabelStep As Double) As Boolean
        minorStep = 0
        majorLabelStep = 0
        If HasFixedSnapTickStepForIndex(index) Then
            minorStep = SnapTickStepForIndex(index)
            Dim majorEvery As Integer
            ApplyRulerMajorEvery(minorStep, minorStep, majorEvery)
            majorLabelStep = minorStep * majorEvery
            Return True
        End If
        Dim majorEveryDyn As Integer
        If Not TryComputeRulerStep(index, vp, minorStep, majorEveryDyn) Then Return False
        majorLabelStep = minorStep * majorEveryDyn
        Return True
    End Function

    Private Function FormatViewportValue(index As Integer, vp As RhinoViewport, v As Double) As String
        Dim minorStep As Double
        Dim majorLabelStep As Double
        If TryGetViewportLabelStep(index, vp, minorStep, majorLabelStep) Then
            Return FormatDisplayValue(v, minorStep)
        End If
        Return v.ToString("G9", Globalization.CultureInfo.InvariantCulture)
    End Function

    ''' <summary>Hide ruler labels only when they would duplicate a snap tick label.</summary>
    Private Function ShouldDrawRulerLabel(args As IGH_PreviewArgs, crv As Curve, index As Integer, tNorm As Double,
                                            stepVal As Double, labelStep As Double) As Boolean
        If SnapParamsForCurve(index).Count = 0 Then Return True

        Dim v As Double = DisplayValueAtNormalized(index, tNorm)
        Dim valueTol As Double = Math.Max(stepVal * 0.3R, labelStep * 0.15R)
        Dim clear2 As Double = SnapLabelClearancePx * SnapLabelClearancePx

        Dim pt As Point3d = crv.PointAt(crv.Domain.ParameterAt(Math.Max(0.0R, Math.Min(1.0R, tNorm))))
        If Not pt.IsValid Then Return True
        Dim sp As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(pt)

        For Each ts As Double In SnapParamsForCurve(index)
            Dim snapV As Double = DisplayValueAtNormalized(index, ts)
            If Math.Abs(v - snapV) <= valueTol Then Return False

            Dim snapPt As Point3d = crv.PointAt(crv.Domain.ParameterAt(ts))
            If Not snapPt.IsValid Then Continue For
            Dim snapSp As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(snapPt)
            Dim dx As Double = snapSp.X - sp.X
            Dim dy As Double = snapSp.Y - sp.Y
            If dx * dx + dy * dy <= clear2 AndAlso Math.Abs(v - snapV) < labelStep * 0.55R Then Return False
        Next
        Return True
    End Function

    ''' <summary>Equal-division ruler ticks that densify as the view zooms in; labels on major ticks when selected.</summary>
    Private Sub DrawRulerTicks(args As IGH_PreviewArgs, index As Integer, crv As Curve, col As Color, selected As Boolean,
                               placedLabels As List(Of Rhino.Geometry.Point2d))
        If Not ShouldShowRulerAnnotations(crv, args.Viewport) Then Return

        Dim stepVal As Double
        Dim majorEvery As Integer
        If HasFixedSnapTickStepForIndex(index) Then
            stepVal = SnapTickStepForIndex(index)
            ApplyRulerMajorEvery(stepVal, stepVal, majorEvery)
        ElseIf Not TryComputeRulerStep(index, args.Viewport, stepVal, majorEvery) Then
            Return
        End If

        Dim tVisLo As Double, tVisHi As Double
        If Not TryVisibleNormalizedRange(crv, args.Viewport, tVisLo, tVisHi) Then Return

        Dim d0 As Double = DisplayValueAtNormalized(index, 0.0R)
        Dim d1 As Double = DisplayValueAtNormalized(index, 1.0R)
        Dim lo As Double = Math.Min(d0, d1)
        Dim hi As Double = Math.Max(d0, d1)

        Dim dVisLo As Double = DisplayValueAtNormalized(index, tVisLo)
        Dim dVisHi As Double = DisplayValueAtNormalized(index, tVisHi)
        Dim drawLo As Double = Math.Min(dVisLo, dVisHi) - stepVal * 2.0R
        Dim drawHi As Double = Math.Max(dVisLo, dVisHi) + stepVal * 2.0R
        drawLo = Math.Max(lo, drawLo)
        drawHi = Math.Min(hi, drawHi)

        Dim n0 As Long = 0
        Dim n1 As Long = -1
        Dim labelStep As Double = stepVal * majorEvery
        Dim attempts As Integer = 0
        Do
            n0 = CLng(Math.Ceiling(drawLo / stepVal - 0.000001R))
            n1 = CLng(Math.Floor(drawHi / stepVal + 0.000001R))
            If n1 < n0 Then Return
            If n1 - n0 + 1 <= RulerMaxTickCount Then Exit Do
            stepVal = SnapRulerStepUp(stepVal * 1.05R)
            ApplyRulerMajorEvery(stepVal, stepVal, majorEvery)
            labelStep = stepVal * majorEvery
            attempts += 1
        Loop While attempts < 24
        If n1 - n0 + 1 > RulerMaxTickCount Then Return

        For n As Long = n0 To n1
            Dim v As Double = n * stepVal
            Dim t As Double
            If Not TryNormalizedFromDisplayValueUnclamped(index, v, t) Then Continue For
            If t < 0.002R OrElse t > 0.998R Then Continue For
            If Not IsCurvePointNearViewport(args.Viewport, crv, t) Then Continue For

            Dim isMajor As Boolean = (((n Mod majorEvery) + majorEvery) Mod majorEvery = 0)
            Dim lbl As String = Nothing
            If isMajor AndAlso selected AndAlso ShouldDrawRulerLabel(args, crv, index, t, stepVal, labelStep) Then
                lbl = FormatDisplayValue(v, labelStep)
            End If
            DrawCurveTick(args, crv, t, If(isMajor, CurveTickKind.Major, CurveTickKind.Minor), col, lbl, placedLabels)
        Next
    End Sub

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        If Curves.Count = 0 Then Return

        Dim selected As Boolean = Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected
        Dim col As Color = If(selected, args.WireColour_Selected, args.WireColour)

        For i As Integer = 0 To Curves.Count - 1
            If Not IsCurveActiveForViewport(i) Then Continue For
            Dim crv As Curve = Curves(i)
            If crv Is Nothing Then Continue For

            Dim showRuler As Boolean = ShouldShowRulerAnnotations(crv, args.Viewport)
            Dim placedLabels As New List(Of Rhino.Geometry.Point2d)
            Dim stepVal As Double = 0
            Dim majorEvery As Integer = 10
            If showRuler Then TryComputeRulerStep(i, args.Viewport, stepVal, majorEvery)
            Dim labelStep As Double = stepVal * majorEvery

            Dim startLabel As String = Nothing
            If showRuler AndAlso selected AndAlso ShouldDrawRulerLabel(args, crv, i, 0.0R, stepVal, labelStep) Then
                startLabel = FormatViewportValue(i, args.Viewport, DisplayValueAtNormalized(i, 0.0R))
            End If
            If showRuler Then DrawCurveTick(args, crv, 0.0R, CurveTickKind.EndCap, col, startLabel, placedLabels)

            Dim endLabel As String = Nothing
            If showRuler AndAlso selected AndAlso ShouldDrawRulerLabel(args, crv, i, 1.0R, stepVal, labelStep) Then
                endLabel = FormatViewportValue(i, args.Viewport, DisplayValueAtNormalized(i, 1.0R))
            End If
            If showRuler Then DrawCurveTick(args, crv, 1.0R, CurveTickKind.EndCap, col, endLabel, placedLabels)

            If showRuler Then DrawRulerTicks(args, i, crv, col, selected, placedLabels)

            If showRuler Then
                For Each ts As Double In SnapParamsForCurve(i)
                    Dim snapLbl As String = If(selected, FormatViewportValue(i, args.Viewport, DisplayValueAtNormalized(i, ts)), Nothing)
                    DrawCurveTick(args, crv, ts, CurveTickKind.SnapValue, col, snapLbl, placedLabels)
                Next
            End If

            Dim pt As Point3d = If(i < Points.Count, Points(i), Point3d.Unset)
            If Not pt.IsValid Then Continue For

            args.Display.DrawPoint(pt, PointStyle.RoundControlPoint, 5, col)

            If showRuler Then
                Dim v As Double = DisplayValue(i)
                Dim label As String = FormatViewportValue(i, args.Viewport, v)
                Dim screenPt As Rhino.Geometry.Point2d = args.Viewport.WorldToClient(pt)
                args.Display.Draw2dText(label, col, New Rhino.Geometry.Point2d(screenPt.X, screenPt.Y - 14.0R), True, 14)
            End If
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim bb As BoundingBox = BoundingBox.Empty
            For i As Integer = 0 To Points.Count - 1
                If Not IsCurveActiveForViewport(i) Then Continue For
                Dim p As Point3d = Points(i)
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
        writer.SetBoolean("CS_SnapTicks", SnappingTicks)
        writer.SetBoolean("CS_StartingPosition", StartingPosition)
        writer.SetBoolean("CS_LockUnselected", LockUnselected)
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

        Dim snapTicks As Boolean = False
        reader.TryGetBoolean("CS_SnapTicks", snapTicks)
        SnappingTicks = snapTicks

        Dim startingPos As Boolean = False
        reader.TryGetBoolean("CS_StartingPosition", startingPos)
        StartingPosition = startingPos

        Dim lockUnsel As Boolean = True
        reader.TryGetBoolean("CS_LockUnselected", lockUnsel)
        LockUnselected = lockUnsel

        ' Register optional inputs before MyBase.Read so archived param data/sources map onto them.
        SyncOptionalInputs()
        VariableParameterMaintenance()

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
    Private _snappingTicks As Boolean
    Private _startingPosition As Boolean
    Private _lockUnselected As Boolean

    Sub New(owner As CurveSliderComp)
        _ownerId = owner.InstanceGuid
        _params = New List(Of Double)(owner.SliderParams)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _real = owner.ShowRealValue
        _customDomain = owner.CustomDomain
        _snapValues = owner.SnapValues
        _snappingTicks = owner.SnappingTicks
        _startingPosition = owner.StartingPosition
        _lockUnselected = owner.LockUnselected
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
        Dim curSnapTicks As Boolean = comp.SnappingTicks
        Dim curStarting As Boolean = comp.StartingPosition
        Dim curLockUnselected As Boolean = comp.LockUnselected
        comp.SetStateFromUndo(_params, _preserve, _proximity, _real, _customDomain, _snapValues, _snappingTicks, _startingPosition, _lockUnselected)
        _params = curParams
        _preserve = curPreserve
        _proximity = curProximity
        _real = curReal
        _customDomain = curCustom
        _snapValues = curSnap
        _snappingTicks = curSnapTicks
        _startingPosition = curStarting
        _lockUnselected = curLockUnselected
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
            If Not Comp.IsCurveActiveForViewport(i) Then Continue For
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
            Dim snapped As Boolean = False
            t = ApplySnapIfClose(_dragIndex, t, e.View, snapped)
            ' Ruler tick quantization (optional); explicit S-input snap values always take priority.
            If Not snapped AndAlso Comp.SnappingTicks Then t = Comp.QuantizeToRulerStep(_dragIndex, t, e.View)
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
    Private Function ApplySnapIfClose(index As Integer, t As Double, view As Rhino.Display.RhinoView, ByRef snapped As Boolean) As Double
        snapped = False
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
        If bestDist <= SnapRadiusPx * SnapRadiusPx Then
            snapped = True
            Return bestT
        End If
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
    Friend Shared Function ConsumeBackdropMouseDown() As Boolean
        Dim f As FormCurveSliderBox = _activeInstance
        If f Is Nothing OrElse f.IsDisposed OrElse Not f.Visible Then Return False
        If f._committing OrElse Not f._outsideDismissReady Then Return False
        If Environment.TickCount < f._suppressBackdropDismissUntil Then Return False
        f.TryDismissFromOutsideRhinoGesture()
        Return True
    End Function

    Friend Shared Sub RequestDismissFromBackdropMouse()
        ConsumeBackdropMouseDown()
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
