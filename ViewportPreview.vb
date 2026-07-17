Imports System.Reflection
Imports Grasshopper.Kernel
Imports Rhino

''' <summary>Shared viewport preview visibility (PSE, Follow selection, component Hidden flag).</summary>
Friend Module ViewportPreview

    Private _followSelectionEnabledProp As PropertyInfo = Nothing
    Private _rhinoDocHandlersAttached As Boolean = False
    Private _rhinoIdleHandlerAttached As Boolean = False
    Private _lastPolledActiveRhinoDocSerial As UInt32 = UInt32.MaxValue
    Private ReadOnly _ghDocHooks As New HashSet(Of Guid)()

    ''' <summary>True when this component's Grasshopper definition should draw in the active Rhino model.</summary>
    Friend Function IsLinkedRhinoDocumentActive(comp As GH_Component) As Boolean
        If comp Is Nothing Then Return False
        Dim ghDoc As GH_Document = comp.OnPingDocument()
        If ghDoc Is Nothing Then Return False
        If Not ghDoc.Enabled Then Return False

        Dim active As RhinoDoc = RhinoDoc.ActiveDoc
        If active Is Nothing Then Return False

        Dim owner As RhinoDoc = GetPreviewRhinoDoc(comp)
        If owner Is Nothing Then owner = ghDoc.RhinoDocument
        If owner Is Nothing Then Return False

        Return ReferenceEquals(owner, active)
    End Function

    Friend Function GetPreviewRhinoDoc(comp As GH_Component) As RhinoDoc
        If comp Is Nothing Then Return Nothing
        Dim gumball As GumballComp = TryCast(comp, GumballComp)
        If gumball IsNot Nothing Then Return gumball.PreviewRhinoDoc
        Dim slider As CurveSliderComp = TryCast(comp, CurveSliderComp)
        If slider IsNot Nothing Then Return slider.PreviewRhinoDoc
        Return Nothing
    End Function

    Friend Sub SetPreviewRhinoDoc(comp As GH_Component, doc As RhinoDoc)
        If comp Is Nothing Then Return
        Dim gumball As GumballComp = TryCast(comp, GumballComp)
        If gumball IsNot Nothing Then
            gumball.SetPreviewRhinoDoc(doc)
            Return
        End If
        Dim slider As CurveSliderComp = TryCast(comp, CurveSliderComp)
        If slider IsNot Nothing Then slider.SetPreviewRhinoDoc(doc)
    End Sub

    Friend Sub ClearPreviewRhinoDoc(comp As GH_Component)
        SetPreviewRhinoDoc(comp, Nothing)
    End Sub

    Friend Sub EnsurePreviewRhinoDoc(comp As GH_Component)
        If comp Is Nothing Then Return
        Dim ghDoc As GH_Document = comp.OnPingDocument()
        If ghDoc Is Nothing OrElse Not ghDoc.Enabled Then Return
        Dim active As RhinoDoc = RhinoDoc.ActiveDoc
        If active Is Nothing Then Return
        Dim owner As RhinoDoc = GetPreviewRhinoDoc(comp)
        If owner Is Nothing OrElse Not IsRhinoDocStillOpen(owner) Then
            SetPreviewRhinoDoc(comp, active)
        End If
    End Sub

    Friend Function IsRhinoDocStillOpen(doc As RhinoDoc) As Boolean
        If doc Is Nothing Then Return False
        Try
            For Each openDoc As RhinoDoc In RhinoDoc.OpenDocuments()
                If ReferenceEquals(openDoc, doc) Then Return True
            Next
        Catch
        End Try
        Return False
    End Function

    Friend Sub EnsureRhinoDocumentLifecycleHandlers()
        If Not _rhinoDocHandlersAttached Then
            AddHandler RhinoDoc.NewDocument, AddressOf OnRhinoDocumentLifecycleChanged
            AddHandler RhinoDoc.EndOpenDocument, AddressOf OnRhinoDocumentLifecycleChanged
            AddHandler RhinoDoc.CloseDocument, AddressOf OnRhinoDocumentLifecycleChanged
            AddHandler RhinoDoc.ActiveDocumentChanged, AddressOf OnRhinoDocumentLifecycleChanged
            _rhinoDocHandlersAttached = True
        End If
        If Not _rhinoIdleHandlerAttached Then
            AddHandler Rhino.RhinoApp.Idle, AddressOf OnRhinoAppIdlePollActiveDocument
            _rhinoIdleHandlerAttached = True
        End If
    End Sub

    Friend Sub EnsureGrasshopperDocumentHooks(ghDoc As GH_Document)
        If ghDoc Is Nothing Then Return
        EnsureRhinoDocumentLifecycleHandlers()
        If _ghDocHooks.Contains(ghDoc.DocumentID) Then Return
        AddHandler ghDoc.EnabledChanged, AddressOf OnGhDocumentEnabledChanged
        _ghDocHooks.Add(ghDoc.DocumentID)
    End Sub

    Private Sub OnGhDocumentEnabledChanged(sender As Object, e As GH_DocEnabledEventArgs)
        Dim ghDoc As GH_Document = TryCast(sender, GH_Document)
        If ghDoc IsNot Nothing AndAlso e.Enabled Then
            Dim active As RhinoDoc = RhinoDoc.ActiveDoc
            If active IsNot Nothing Then
                For Each obj As IGH_DocumentObject In ghDoc.Objects
                    Dim comp As GH_Component = TryCast(obj, GH_Component)
                    If comp IsNot Nothing Then SetPreviewRhinoDoc(comp, active)
                Next
            End If
        End If
        If ghDoc IsNot Nothing Then
            SyncViewportToolsInDocument(ghDoc)
        Else
            SyncAllViewportTools()
        End If
        RedrawAllOpenRhinoDocuments()
    End Sub

    Private Sub OnRhinoDocumentLifecycleChanged(sender As Object, e As DocumentEventArgs)
        UpdatePreviewRhinoDocsForActiveModel()
        SyncAllViewportTools()
        RedrawAllOpenRhinoDocuments()
    End Sub

    Private Sub OnRhinoAppIdlePollActiveDocument(sender As Object, e As EventArgs)
        Try
            Dim active As RhinoDoc = RhinoDoc.ActiveDoc
            Dim serial As UInt32 = If(active IsNot Nothing, active.RuntimeSerialNumber, UInt32.MaxValue)
            If serial = _lastPolledActiveRhinoDocSerial Then Return
            _lastPolledActiveRhinoDocSerial = serial
            UpdatePreviewRhinoDocsForActiveModel()
            SyncAllViewportTools()
            RedrawAllOpenRhinoDocuments()
        Catch
        End Try
    End Sub

    Private Sub UpdatePreviewRhinoDocsForActiveModel()
        Dim active As RhinoDoc = RhinoDoc.ActiveDoc
        If active Is Nothing Then Return
        Try
            For Each ghDoc As GH_Document In Grasshopper.Instances.DocumentServer
                If ghDoc Is Nothing OrElse Not ghDoc.Enabled Then Continue For
                For Each obj As IGH_DocumentObject In ghDoc.Objects
                    Dim comp As GH_Component = TryCast(obj, GH_Component)
                    If comp IsNot Nothing Then SetPreviewRhinoDoc(comp, active)
                Next
            Next
        Catch
        End Try
    End Sub

    Private Sub SyncViewportToolsInDocument(ghDoc As GH_Document)
        If ghDoc Is Nothing Then Return
        Try
            For Each obj As IGH_DocumentObject In ghDoc.Objects
                Dim gumball As GumballComp = TryCast(obj, GumballComp)
                If gumball IsNot Nothing Then
                    gumball.SyncGumballVisibility()
                    Continue For
                End If
                Dim slider As CurveSliderComp = TryCast(obj, CurveSliderComp)
                If slider IsNot Nothing Then slider.SyncViewportInteraction()
            Next
        Catch
        End Try
    End Sub

    Private Sub SyncAllViewportTools()
        Try
            For Each ghDoc As GH_Document In Grasshopper.Instances.DocumentServer
                SyncViewportToolsInDocument(ghDoc)
            Next
        Catch
        End Try
    End Sub

    Friend Sub RedrawAllOpenRhinoDocuments()
        Try
            For Each doc As RhinoDoc In RhinoDoc.OpenDocuments()
                If doc IsNot Nothing Then doc.Views.Redraw()
            Next
        Catch
        End Try
    End Sub

    Friend Sub TryRedrawLinkedOrActiveDoc(comp As GH_Component)
        Try
            Dim rh As RhinoDoc = GetPreviewRhinoDoc(comp)
            If rh Is Nothing Then
                Dim ghDoc As GH_Document = If(comp IsNot Nothing, comp.OnPingDocument(), Nothing)
                If ghDoc IsNot Nothing Then rh = ghDoc.RhinoDocument
            End If
            If rh Is Nothing Then rh = RhinoDoc.ActiveDoc
            If rh IsNot Nothing Then rh.Views.Redraw()
        Catch
        End Try
    End Sub

    ''' <summary>True when Grasshopper (or Follow selection) would draw this object in Rhino viewports.</summary>
    Friend Function IsEffectivelyPreviewed(comp As GH_Component) As Boolean
        If comp Is Nothing Then Return False
        If Not comp.Hidden Then Return True

        Dim selected As Boolean = comp.Attributes IsNot Nothing AndAlso comp.Attributes.Selected
        If Not selected Then Return False

        Try
            Dim doc As GH_Document = comp.OnPingDocument()
            If doc Is Nothing Then Return False

            If doc.PreviewFilter = GH_PreviewFilter.Selected Then Return True

            If doc.PreviewFilter = GH_PreviewFilter.None AndAlso
                doc.PreviewMode <> GH_PreviewMode.Disabled AndAlso
                IsFollowSelectionActive() Then
                Return True
            End If
        Catch
        End Try
        Return False
    End Function

    Private Function IsFollowSelectionActive() As Boolean
        Dim prop As PropertyInfo = FollowSelectionEnabledProperty()
        If prop Is Nothing Then Return False
        Try
            Return CBool(prop.GetValue(Nothing))
        Catch
            Return False
        End Try
    End Function

    Private Function FollowSelectionEnabledProperty() As PropertyInfo
        If _followSelectionEnabledProp IsNot Nothing Then Return _followSelectionEnabledProp
        Try
            For Each asm As Assembly In AppDomain.CurrentDomain.GetAssemblies()
                If Not String.Equals(asm.GetName().Name, "FollowSelection", StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim t As Type = asm.GetType("SelectionPreview.FollowSelectionViewportConduit")
                If t Is Nothing Then Continue For
                _followSelectionEnabledProp = t.GetProperty(
                    "FeatureEnabled",
                    BindingFlags.Public Or BindingFlags.NonPublic Or BindingFlags.Static)
                Return _followSelectionEnabledProp
            Next
        Catch
        End Try
        Return Nothing
    End Function

End Module
