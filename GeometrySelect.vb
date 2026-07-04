Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Reflection
Imports System.Windows.Forms
Imports Grasshopper
Imports Grasshopper.Kernel
Imports Grasshopper.Kernel.Data
Imports Grasshopper.GUI.Canvas
Imports Grasshopper.Kernel.Types
Imports Grasshopper.Rhinoceros.Model
Imports Rhino.Display
Imports Rhino.Geometry

''' <summary>Block instance geometry traversal in world space (Rhino doc + Grasshopper model blocks).</summary>
Friend Module SelectorInstanceUtil

    Friend Sub EnsureInstanceLoaded(ghInst As GH_InstanceReference)
        If ghInst Is Nothing Then Return
        Try
            ghInst.LoadGeometry()
        Catch
        End Try
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then ghInst.LoadGeometry(doc)
        Catch
        End Try
    End Sub

    ''' <summary>Build InstanceReferenceGeometry when GH_InstanceReference.Value is not yet populated.</summary>
    Friend Function CreateInstanceReferenceGeometry(ghInst As GH_InstanceReference) As InstanceReferenceGeometry
        If ghInst Is Nothing Then Return Nothing
        EnsureInstanceLoaded(ghInst)
        If ghInst.Value IsNot Nothing Then Return DirectCast(ghInst.Value.Duplicate(), InstanceReferenceGeometry)

        Dim xform As Transform = Transform.Identity
        Try
            If ghInst.ModelTransform.IsValid Then xform = ghInst.ModelTransform
        Catch
        End Try

        Dim defId As Guid = Guid.Empty
        Try
            If ghInst.InstanceDefinition IsNot Nothing Then defId = ghInst.InstanceDefinition.Id
        Catch
        End Try

        If defId = Guid.Empty AndAlso ghInst.ReferenceID <> Guid.Empty Then
            Try
                Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
                If doc IsNot Nothing Then
                    Dim ro As Rhino.DocObjects.RhinoObject = doc.Objects.FindId(ghInst.ReferenceID)
                    Dim instObj As Rhino.DocObjects.InstanceObject = TryCast(ro, Rhino.DocObjects.InstanceObject)
                    If instObj IsNot Nothing Then
                        If instObj.InstanceDefinition IsNot Nothing Then defId = instObj.InstanceDefinition.Id
                        If instObj.InstanceXform.IsValid Then xform = instObj.InstanceXform
                    End If
                End If
            Catch
            End Try
        End If

        If defId = Guid.Empty Then Return Nothing
        Return New InstanceReferenceGeometry(defId, xform)
    End Function

    Friend Function GetInstanceWorldBoundingBox(iref As InstanceReferenceGeometry, ghInst As GH_InstanceReference) As BoundingBox
        Dim bb As BoundingBox = BoundingBox.Empty
        ForEachWorldPiece(iref, ghInst, Sub(piece)
                                            If piece Is Nothing Then Return
                                            Try
                                                Dim pb As BoundingBox = piece.GetBoundingBox(True)
                                                If pb.IsValid Then bb.Union(pb)
                                            Catch
                                            End Try
                                        End Sub)
        If bb.IsValid Then Return bb

        If iref IsNot Nothing Then
            Try
                bb = iref.GetBoundingBox(True)
                If bb.IsValid Then Return bb
            Catch
            End Try
        End If

        If ghInst IsNot Nothing Then
            Try
                bb = ghInst.Boundingbox
                If bb.IsValid Then Return bb
            Catch
            End Try
            Try
                bb = ghInst.GetBoundingBox(Transform.Identity)
                If bb.IsValid Then Return bb
            Catch
            End Try
        End If

        Return BoundingBox.Unset
    End Function

    Friend Function InstanceTransform(iref As InstanceReferenceGeometry, ghInst As GH_InstanceReference) As Transform
        If ghInst IsNot Nothing AndAlso ghInst.Value IsNot Nothing AndAlso ghInst.Value.Xform.IsValid Then
            Return ghInst.Value.Xform
        End If
        If iref IsNot Nothing AndAlso iref.Xform.IsValid Then Return iref.Xform
        Try
            If ghInst IsNot Nothing AndAlso ghInst.ModelTransform.IsValid Then Return ghInst.ModelTransform
        Catch
        End Try
        Return Transform.Identity
    End Function

    Friend Sub ForEachWorldPiece(iref As InstanceReferenceGeometry, ghInst As GH_InstanceReference, action As Action(Of GeometryBase))
        If action Is Nothing Then Return
        EnsureInstanceLoaded(ghInst)
        Dim xform As Transform = InstanceTransform(iref, ghInst)

        Dim modelDef As ModelInstanceDefinition = Nothing
        If ghInst IsNot Nothing Then modelDef = ghInst.InstanceDefinition

        If modelDef IsNot Nothing Then
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then
                Try
                    Dim rhFromModel As Rhino.DocObjects.InstanceDefinition = doc.InstanceDefinitions.FindId(modelDef.Id)
                    If rhFromModel IsNot Nothing Then
                        TraverseRhinoDefinition(rhFromModel, xform, action)
                        Return
                    End If
                Catch
                End Try
            End If
            If TraverseModelDefinition(modelDef, xform, action) Then Return
        End If

        Dim rhDef As Rhino.DocObjects.InstanceDefinition = ResolveRhinoInstanceDefinition(iref, ghInst)
        If rhDef IsNot Nothing Then
            TraverseRhinoDefinition(rhDef, xform, action)
        End If
    End Sub

    Private Function ResolveRhinoInstanceDefinition(iref As InstanceReferenceGeometry, ghInst As GH_InstanceReference) As Rhino.DocObjects.InstanceDefinition
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc Is Nothing Then Return Nothing

            If ghInst IsNot Nothing AndAlso ghInst.ReferenceID <> Guid.Empty Then
                Dim ro As Rhino.DocObjects.RhinoObject = doc.Objects.FindId(ghInst.ReferenceID)
                Dim instObj As Rhino.DocObjects.InstanceObject = TryCast(ro, Rhino.DocObjects.InstanceObject)
                If instObj IsNot Nothing AndAlso instObj.InstanceDefinition IsNot Nothing Then
                    Return instObj.InstanceDefinition
                End If
            End If

            If iref IsNot Nothing Then
                Dim d As Rhino.DocObjects.InstanceDefinition = doc.InstanceDefinitions.FindId(iref.ParentIdefId)
                If d IsNot Nothing Then Return d
            End If
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>Rhino block definition traversal: nested InstanceObjects compose InstanceXform (same as Rhino selection internals).</summary>
    Private Sub TraverseRhinoDefinition(idef As Rhino.DocObjects.InstanceDefinition, parentXform As Transform, action As Action(Of GeometryBase))
        If idef Is Nothing Then Return
        For Each ro As Rhino.DocObjects.RhinoObject In idef.GetObjects()
            Dim nestedInst As Rhino.DocObjects.InstanceObject = TryCast(ro, Rhino.DocObjects.InstanceObject)
            If nestedInst IsNot Nothing Then
                Dim childXform As Transform = parentXform * nestedInst.InstanceXform
                TraverseRhinoDefinition(nestedInst.InstanceDefinition, childXform, action)
                Continue For
            End If

            If ro.Geometry IsNot Nothing Then
                Dim dup As GeometryBase = ro.Geometry.Duplicate()
                If dup IsNot Nothing Then
                    dup.Transform(parentXform)
                    Try
                        action(dup)
                    Finally
                        dup.Dispose()
                    End Try
                End If
            End If

            Try
                Dim meshes As Mesh() = ro.GetMeshes(MeshType.Render)
                If meshes IsNot Nothing Then
                    For Each m As Mesh In meshes
                        If m Is Nothing Then Continue For
                        Dim dm As Mesh = m.DuplicateMesh()
                        dm.Transform(parentXform)
                        Try
                            action(dm)
                        Finally
                            dm.Dispose()
                        End Try
                    Next
                End If
            Catch
            End Try
        Next
    End Sub

  Private Function TraverseModelDefinition(modelDef As ModelInstanceDefinition, parentXform As Transform, action As Action(Of GeometryBase)) As Boolean
        If modelDef Is Nothing OrElse modelDef.Objects Is Nothing OrElse modelDef.Objects.Count = 0 Then Return False
        For Each mo As ModelObject In modelDef.Objects
            If mo Is Nothing Then Continue For
            If mo.ObjectType = Rhino.DocObjects.ObjectType.InstanceReference Then
                Dim nestedGh As GH_InstanceReference = TryGetInstanceReferenceFromModelObject(mo)
                If nestedGh IsNot Nothing Then
                    EnsureInstanceLoaded(nestedGh)
                    Dim nestedXform As Transform = parentXform
                    If nestedGh.Value IsNot Nothing AndAlso nestedGh.Value.Xform.IsValid Then
                        nestedXform = parentXform * nestedGh.Value.Xform
                    End If
                    If nestedGh.InstanceDefinition IsNot Nothing Then
                        TraverseModelDefinition(nestedGh.InstanceDefinition, nestedXform, action)
                    Else
                        ForEachWorldPiece(nestedGh.Value, nestedGh, action)
                    End If
                End If
            Else
                ForEachModelObjectGeometry(mo, parentXform, action)
            End If
        Next
        Return True
    End Function

    Private Function TryGetInstanceReferenceFromModelObject(mo As ModelObject) As GH_InstanceReference
        If mo Is Nothing Then Return Nothing
        Try
            Dim gh As GH_InstanceReference = Nothing
            If GH_Convert.ToGHInstanceReference_Primary(mo, gh) AndAlso gh IsNot Nothing Then Return gh
        Catch
        End Try
        Return Nothing
    End Function

    Private Sub ForEachModelObjectGeometry(mo As ModelObject, xform As Transform, action As Action(Of GeometryBase))
        If mo Is Nothing Then Return
        Dim handled As Boolean = False

        Try
            Dim gb As GeometryBase = GH_Convert.ToGeometryBase(mo)
            If gb IsNot Nothing Then
                Dim dup As GeometryBase = gb.Duplicate()
                dup.Transform(xform)
                Try
                    action(dup)
                    handled = True
                Finally
                    dup.Dispose()
                End Try
            End If
        Catch
        End Try
        If handled Then Return

        Try
            Dim goo As IGH_GeometricGoo = GH_Convert.ToGeometricGoo(mo)
            If goo IsNot Nothing Then
                Dim gb2 As GeometryBase = GH_Convert.ToGeometryBase(goo)
                If gb2 IsNot Nothing Then
                    Dim dup As GeometryBase = gb2.Duplicate()
                    dup.Transform(xform)
                    Try
                        action(dup)
                        handled = True
                    Finally
                        dup.Dispose()
                    End Try
                End If
            End If
        Catch
        End Try
        If handled Then Return

        ' ModelObject preview geometry (IGH_PreviewData) when GH_Convert paths fail.
        Try
            Dim box As BoundingBox = mo.GetBoundingBox(Transform.Identity)
            If box.IsValid Then
                Dim brep As Brep = Brep.CreateFromBox(box)
                If brep IsNot Nothing Then
                    brep.Transform(xform)
                    Try
                        action(brep)
                    Finally
                        brep.Dispose()
                    End Try
                End If
            End If
        Catch
        End Try
    End Sub

End Module

''' <summary>Fingerprint used for proximity matching and save-shifted restoration.</summary>
Friend Structure GeometryProximityKey
    Public ObjectType As Integer
    Public Center As Point3d
    Public Diagonal As Double
    Public InstanceDefId As Guid
    Public ReferenceId As Guid
End Structure

''' <summary>Viewport geometry picker: click items to toggle selection; outputs indices and geometry.</summary>
Public Class GeometrySelectComp
    Inherits GH_Component
    Implements IGH_VariableParameterComponent

    Public Sub New()
        MyBase.New("Selector", "Selector",
                   "Click geometry or block instances in the viewport (component selected on canvas) to toggle selection. Outputs flat indices and geometry.",
                   "Params", "Util")
        PickMouse = New GeometrySelectMouse(Me)
    End Sub

#Region "Component overrides"

    Private Shared ReadOnly IconResourceName As String = "GumballGH.GeometrySelectIcon.png"

    Private Shared _icon As Bitmap

    Private Shared Function LoadEmbeddedIcon() As Bitmap
        Const target As Integer = 24
        Try
            Dim asm As Assembly = Assembly.GetExecutingAssembly()
            Using src As Stream = asm.GetManifestResourceStream(IconResourceName)
                If src Is Nothing Then Return Nothing
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
            Return Nothing
        End Try
    End Function

    Protected Overrides ReadOnly Property Icon() As Bitmap
        Get
            If _icon Is Nothing Then _icon = LoadEmbeddedIcon()
            Return _icon
        End Get
    End Property

    Public Overrides ReadOnly Property ComponentGuid() As Guid
        Get
            Return New Guid("{a8f3c2e1-5b4d-4a9f-8e1c-3d7f6b2a9c54}")
        End Get
    End Property

    Protected Overrides Sub RegisterInputParams(pManager As GH_Component.GH_InputParamManager)
        pManager.AddGeometryParameter("Geometry", "G", "Geometry or block instances to pick from (tree).", GH_ParamAccess.tree)
    End Sub

    Protected Overrides Sub RegisterOutputParams(pManager As GH_Component.GH_OutputParamManager)
        pManager.AddIntegerParameter("Index", "I", "Index of each item within its input tree branch (use with the matching output path and List Item).", GH_ParamAccess.tree)
        pManager.AddGeometryParameter("Geometry", "G", "Selected geometry.", GH_ParamAccess.tree)
    End Sub

    Public Overrides Sub AddedToDocument(document As GH_Document)
        MyBase.AddedToDocument(document)
        VariableParameterMaintenance()
    End Sub

    Public Overrides Sub CreateAttributes()
        m_attributes = New GeometrySelectCompAtt(Me)
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

    Protected Overrides Sub AppendAdditionalComponentMenuItems(menu As ToolStripDropDown)
        Dim preselect As ToolStripMenuItem = Menu_AppendItem(menu, "Preselected indices", AddressOf Menu_PreselectedIndices, True, Me.PreselectedIndices)
        preselect.ToolTipText = "Adds an optional Ix input: branch-local indices per geometry path to preselect. Viewport picking can override."

        Dim split As ToolStripMenuItem = Menu_AppendItem(menu, "Two lists (selected / unselected)", AddressOf Menu_OutputSplit, True, Me.OutputSplitLists)
        split.ToolTipText = "When on, adds extra outputs for unselected indices and geometry. Selected outputs are unchanged."

        Dim nulls As ToolStripMenuItem = Menu_AppendItem(menu, "Output nulls", AddressOf Menu_OutputNulls, True, Me.OutputNulls)
        nulls.ToolTipText = "Mirror the input tree on all active outputs: null where an item is not in that list (unselected on I/G, selected on Iu/Gu)."

        Menu_AppendSeparator(menu)

        Dim preserve As ToolStripMenuItem = Menu_AppendItem(menu, "Preserve changes", AddressOf Menu_PreserveChanges, True, Me.PreserveChanges)
        preserve.ToolTipText = "Keep selection flags per item index when upstream geometry changes."

        Dim proximity As ToolStripMenuItem = Menu_AppendItem(menu, "Proximity cache", AddressOf Menu_ProximityCache, True, Me.ProximityCache)
        proximity.ToolTipText = "When the list changes, re-attach each selection to the nearest cached geometry instead of the list index."

        Dim saveShifted As ToolStripMenuItem = Menu_AppendItem(menu, "Save shifted", AddressOf Menu_SaveShifted, True, Me.SaveShifted)
        saveShifted.Enabled = Me.ProximityCache
        saveShifted.ToolTipText = "When proximity cache is on, remember selections for geometry that leaves the list and restore them if it returns."

        Dim cc As ToolStripMenuItem = Menu_AppendItem(menu, "Clear cache", AddressOf Menu_ClearCache, True)
        cc.ToolTipText = "Clear all selections."

        Menu_AppendSeparator(menu)

        Dim lockUnsel As ToolStripMenuItem = Menu_AppendItem(menu, "Lock unselected", AddressOf Menu_LockUnselected, True, Me.LockUnselected)
        lockUnsel.ToolTipText = "When on, viewport picking works only while this component is selected on the Grasshopper canvas."
    End Sub

    Private Sub Menu_PreselectedIndices()
        RecordUndoEvent("Selector Preselected Indices", New GeometrySelectUndo(Me))
        PreselectedIndices = Not PreselectedIndices
        SyncOutputParameters()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_OutputSplit()
        RecordUndoEvent("Selector Output Mode", New GeometrySelectUndo(Me))
        OutputSplitLists = Not OutputSplitLists
        SyncOutputParameters()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_OutputNulls()
        RecordUndoEvent("Selector Output Nulls", New GeometrySelectUndo(Me))
        OutputNulls = Not OutputNulls
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_PreserveChanges()
        RecordUndoEvent("Selector Preserve", New GeometrySelectUndo(Me))
        PreserveChanges = Not PreserveChanges
    End Sub

    Private Sub Menu_ProximityCache()
        RecordUndoEvent("Selector Proximity", New GeometrySelectUndo(Me))
        ProximityCache = Not ProximityCache
    End Sub

    Private Sub Menu_SaveShifted()
        If Not ProximityCache Then Return
        RecordUndoEvent("Selector Save Shifted", New GeometrySelectUndo(Me))
        SaveShifted = Not SaveShifted
    End Sub

    Public Sub Menu_ClearCache()
        RecordUndoEvent("Selector Clear Cache", New GeometrySelectUndo(Me))
        Selected.Clear()
        ShiftedSelectionKeys.Clear()
        CacheGeometries = Nothing
        CacheInstanceGoos = Nothing
        CacheItemPaths = Nothing
        CacheItemBranchIndices = Nothing
        CachePreselectTree = Nothing
        Me.ClearData()
        Me.ExpireSolution(True)
    End Sub

    Private Sub Menu_LockUnselected()
        RecordUndoEvent("Selector Lock Unselected", New GeometrySelectUndo(Me))
        LockUnselected = Not LockUnselected
        SyncMouse()
    End Sub

#End Region

#Region "State"

    ''' <summary>Duplicated input geometry per flattened leaf (Nothing preserved).</summary>
    Friend Geometries As New List(Of GeometryBase)

    ''' <summary>Input tree path per flattened leaf.</summary>
    Friend ItemPaths As New List(Of GH_Path)

    ''' <summary>Index within the input branch at ItemPaths (parallel to Geometries).</summary>
    Friend ItemBranchIndices As New List(Of Integer)

    ''' <summary>Selection flag per flattened leaf.</summary>
    Friend Selected As New List(Of Boolean)

    ''' <summary>Parallel GH instance wrappers when input items are block instances.</summary>
    Friend InstanceGoos As New List(Of GH_InstanceReference)

    ''' <summary>Cached geometry from the last upstream change detection.</summary>
    Private CacheGeometries As List(Of GeometryBase) = Nothing

    ''' <summary>Parallel cached instance wrappers for proximity matching.</summary>
    Private CacheInstanceGoos As List(Of GH_InstanceReference) = Nothing

    ''' <summary>Cached input paths / branch indices aligned with CacheGeometries.</summary>
    Private CacheItemPaths As List(Of GH_Path) = Nothing
    Private CacheItemBranchIndices As List(Of Integer) = Nothing

    ''' <summary>Last applied preselection input (detect wire changes without re-applying on every solve).</summary>
    Private CachePreselectTree As GH_Structure(Of GH_Integer) = Nothing

    ''' <summary>Proximity fingerprints for selected geometry that left the input list (Save shifted).</summary>
    Friend ShiftedSelectionKeys As New List(Of GeometryProximityKey)

    Public PreserveChanges As Boolean = True
    Public ProximityCache As Boolean = False
    Public SaveShifted As Boolean = False
    Public LockUnselected As Boolean = True

    ''' <summary>When true, optional Ix input supplies branch-local indices to preselect.</summary>
    Public PreselectedIndices As Boolean = False

    ''' <summary>When true, extra outputs are added for unselected indices and geometry.</summary>
    Public OutputSplitLists As Boolean = False

    ''' <summary>When true and not split mode, mirror input structure with nulls for unselected items.</summary>
    Public OutputNulls As Boolean = False

    Friend PickMouse As GeometrySelectMouse

    Friend Sub SetSelectionFromUndo(newSelected As List(Of Boolean), newPreserve As Boolean, newProximity As Boolean,
                                    newSaveShifted As Boolean, newShiftedKeys As List(Of GeometryProximityKey),
                                    newPreselected As Boolean, newSplit As Boolean, newNulls As Boolean, newLockUnselected As Boolean)
        Selected = New List(Of Boolean)(newSelected)
        PreserveChanges = newPreserve
        ProximityCache = newProximity
        SaveShifted = newSaveShifted
        ShiftedSelectionKeys = CloneShiftedKeyList(newShiftedKeys)
        PreselectedIndices = newPreselected
        OutputSplitLists = newSplit
        OutputNulls = newNulls
        LockUnselected = newLockUnselected
        SyncOutputParameters()
        SyncMouse()
        Me.ExpireSolution(True)
    End Sub

    Friend Sub SyncOutputParameters()
        If Params Is Nothing Then Return
        VariableParameterMaintenance()
        Params.OnParametersChanged()
    End Sub

    Friend Enum SelectionApplyMode
        Toggle
        SelectOnly
        DeselectOnly
    End Enum

    Friend Sub ToggleSelection(index As Integer)
        ApplySelections(New Integer() {index}, SelectionApplyMode.Toggle)
    End Sub

    Friend Sub ToggleSelections(indices As IEnumerable(Of Integer))
        ApplySelections(indices, SelectionApplyMode.Toggle)
    End Sub

    Friend Sub ApplySelections(indices As IEnumerable(Of Integer), mode As SelectionApplyMode)
        If indices Is Nothing Then Return
        Dim toApply As New List(Of Integer)
        For Each index As Integer In indices
            If index < 0 OrElse index >= Geometries.Count Then Continue For
            Dim hasGeom As Boolean = Geometries(index) IsNot Nothing
            Dim hasInst As Boolean = index < InstanceGoos.Count AndAlso InstanceGoos(index) IsNot Nothing
            If Not hasGeom AndAlso Not hasInst Then Continue For
            toApply.Add(index)
        Next
        If toApply.Count = 0 Then Return

        RecordUndoEvent("Selector Toggle", New GeometrySelectUndo(Me))
        While Selected.Count < Geometries.Count
            Selected.Add(False)
        End While
        For Each index As Integer In toApply
            Dim newSel As Boolean
            Select Case mode
                Case SelectionApplyMode.SelectOnly
                    newSel = True
                Case SelectionApplyMode.DeselectOnly
                    newSel = False
                Case Else
                    newSel = Not Selected(index)
            End Select
            Selected(index) = newSel
            If ProximityCache AndAlso SaveShifted AndAlso Not newSel Then
                Dim inst As GH_InstanceReference = If(index < InstanceGoos.Count, InstanceGoos(index), Nothing)
                Dim key As GeometryProximityKey = Nothing
                If TryGetProximityKey(Geometries(index), inst, key) Then
                    RemoveShiftedKeysMatching(key)
                End If
            End If
        Next
        Me.ExpireSolution(True)
        Try
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
        Catch
        End Try
    End Sub

    Friend RectSelectActive As Boolean
    Friend RectSelectStart As Drawing.Point
    Friend RectSelectEnd As Drawing.Point

    Friend Sub SetRectSelectState(active As Boolean, startPt As Drawing.Point, endPt As Drawing.Point)
        RectSelectActive = active
        RectSelectStart = startPt
        RectSelectEnd = endPt
    End Sub

    Private Sub ShutDownInteraction()
        If PickMouse IsNot Nothing Then PickMouse.Enabled = False
    End Sub

    Friend Sub SyncMouse()
        Dim selectionOk As Boolean = Me.Attributes IsNot Nothing AndAlso (Not LockUnselected OrElse Me.Attributes.Selected)
        Dim want As Boolean =
            selectionOk AndAlso
            Not Me.Locked AndAlso
            Not Me.Hidden AndAlso
            Geometries.Count > 0
        If PickMouse IsNot Nothing Then
            If PickMouse.Enabled <> want Then PickMouse.Enabled = want
        End If
    End Sub

#End Region

#Region "Solve"

    Private Shared Function TryGetProximityKey(g As GeometryBase, inst As GH_InstanceReference, ByRef key As GeometryProximityKey) As Boolean
        key = New GeometryProximityKey With {
            .ObjectType = 0,
            .Center = Point3d.Unset,
            .Diagonal = 0.0R,
            .InstanceDefId = Guid.Empty,
            .ReferenceId = Guid.Empty
        }

        If inst IsNot Nothing Then
            Try
                If inst.ReferenceID <> Guid.Empty Then key.ReferenceId = inst.ReferenceID
            Catch
            End Try
        End If

        Dim iref As InstanceReferenceGeometry = TryCast(g, InstanceReferenceGeometry)
        If iref Is Nothing AndAlso inst IsNot Nothing Then
            iref = SelectorInstanceUtil.CreateInstanceReferenceGeometry(inst)
        End If

        If iref IsNot Nothing OrElse inst IsNot Nothing Then
            key.ObjectType = CInt(Rhino.DocObjects.ObjectType.InstanceReference)
            If iref IsNot Nothing Then
                Try
                    key.InstanceDefId = iref.ParentIdefId
                Catch
                End Try
            End If
            If key.InstanceDefId = Guid.Empty AndAlso inst IsNot Nothing Then
                Try
                    If inst.InstanceDefinition IsNot Nothing Then key.InstanceDefId = inst.InstanceDefinition.Id
                Catch
                End Try
            End If
            Dim bb As BoundingBox = SelectorInstanceUtil.GetInstanceWorldBoundingBox(iref, inst)
            If bb.IsValid Then
                key.Center = bb.Center
                key.Diagonal = bb.Diagonal.Length
                Return key.Center.IsValid
            End If
        End If

        If g Is Nothing Then Return key.ReferenceId <> Guid.Empty

        key.ObjectType = CInt(g.ObjectType)
        Dim gbb As BoundingBox = g.GetBoundingBox(True)
        If gbb.IsValid Then
            key.Center = gbb.Center
            key.Diagonal = gbb.Diagonal.Length
            Return key.Center.IsValid
        End If

        Return False
    End Function

    Private Shared Function ProximityKeysSimilar(a As GeometryProximityKey, b As GeometryProximityKey, tol As Double) As Boolean
        If a.ObjectType <> b.ObjectType Then Return False
        If a.ReferenceId <> Guid.Empty AndAlso b.ReferenceId <> Guid.Empty Then Return a.ReferenceId = b.ReferenceId
        If a.InstanceDefId <> Guid.Empty AndAlso b.InstanceDefId <> Guid.Empty AndAlso a.InstanceDefId <> b.InstanceDefId Then Return False
        If Not a.Center.IsValid OrElse Not b.Center.IsValid Then Return False
        If a.Center.DistanceTo(b.Center) > tol Then Return False
        If Math.Abs(a.Diagonal - b.Diagonal) > tol Then Return False
        Return True
    End Function

    Private Shared Function ModelAbsoluteTolerance() As Double
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then Return doc.ModelAbsoluteTolerance
        Catch
        End Try
        Return 0.001
    End Function

    ''' <summary>Max center distance to treat two items as the same object after a list change (not a forced nearest-neighbor).</summary>
    Private Shared Function MaxProximityMatchDistance(a As GeometryProximityKey, b As GeometryProximityKey) As Double
        Dim docTol As Double = ModelAbsoluteTolerance()
        Dim minDiag As Double = Math.Min(Math.Max(a.Diagonal, docTol), Math.Max(b.Diagonal, docTol))
        Return Math.Max(docTol * 10.0R, minDiag * 0.5R)
    End Function

    Private Shared Function IsWithinProximityMatch(a As GeometryProximityKey, b As GeometryProximityKey) As Boolean
        If Not a.Center.IsValid OrElse Not b.Center.IsValid Then Return False
        If a.Center.DistanceTo(b.Center) > MaxProximityMatchDistance(a, b) Then Return False
        Dim maxDiag As Double = Math.Max(a.Diagonal, b.Diagonal)
        Dim docTol As Double = ModelAbsoluteTolerance()
        If maxDiag > docTol AndAlso Math.Abs(a.Diagonal - b.Diagonal) > maxDiag * 0.5R Then Return False
        Return True
    End Function

    Private Shared Function ProximityMatchScore(oldKey As GeometryProximityKey, newKey As GeometryProximityKey) As Double
        If oldKey.ObjectType <> newKey.ObjectType Then Return Double.PositiveInfinity
        If oldKey.ReferenceId <> Guid.Empty AndAlso newKey.ReferenceId <> Guid.Empty Then
            If oldKey.ReferenceId = newKey.ReferenceId Then Return 0.0R
            Return Double.PositiveInfinity
        End If
        If oldKey.InstanceDefId <> Guid.Empty AndAlso newKey.InstanceDefId <> Guid.Empty AndAlso oldKey.InstanceDefId <> newKey.InstanceDefId Then
            Return Double.PositiveInfinity
        End If
        If Not oldKey.Center.IsValid OrElse Not newKey.Center.IsValid Then Return Double.PositiveInfinity
        Dim posScore As Double = oldKey.Center.DistanceToSquared(newKey.Center)
        Dim sizeDelta As Double = Math.Abs(oldKey.Diagonal - newKey.Diagonal)
        Return posScore + sizeDelta * sizeDelta
    End Function

    Private Shared Function PathsEqual(a As GH_Path, b As GH_Path) As Boolean
        If a Is Nothing AndAlso b Is Nothing Then Return True
        If a Is Nothing OrElse b Is Nothing Then Return False
        Return a.Equals(b)
    End Function

    Private Shared Function SlotMetadataEqual(aPaths As List(Of GH_Path), aBranch As List(Of Integer),
                                              bPaths As List(Of GH_Path), bBranch As List(Of Integer)) As Boolean
        If aPaths Is Nothing OrElse bPaths Is Nothing OrElse aBranch Is Nothing OrElse bBranch Is Nothing Then Return False
        If aPaths.Count <> bPaths.Count OrElse aPaths.Count <> aBranch.Count OrElse bPaths.Count <> bBranch.Count Then Return False
        For i As Integer = 0 To aPaths.Count - 1
            If Not PathsEqual(aPaths(i), bPaths(i)) Then Return False
            If aBranch(i) <> bBranch(i) Then Return False
        Next
        Return True
    End Function

    Private Shared Function GeometriesEqual(a As List(Of GeometryBase), b As List(Of GeometryBase),
                                           aInst As List(Of GH_InstanceReference), bInst As List(Of GH_InstanceReference),
                                           aPaths As List(Of GH_Path), bPaths As List(Of GH_Path),
                                           aBranch As List(Of Integer), bBranch As List(Of Integer)) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return False
        If a.Count <> b.Count Then Return False
        If Not SlotMetadataEqual(aPaths, aBranch, bPaths, bBranch) Then Return False
        Const tol As Double = 0.0001
        For i As Integer = 0 To a.Count - 1
            Dim instA As GH_InstanceReference = If(aInst IsNot Nothing AndAlso i < aInst.Count, aInst(i), Nothing)
            Dim instB As GH_InstanceReference = If(bInst IsNot Nothing AndAlso i < bInst.Count, bInst(i), Nothing)
            Dim ka As GeometryProximityKey = Nothing
            Dim kb As GeometryProximityKey = Nothing
            Dim okA As Boolean = TryGetProximityKey(a(i), instA, ka)
            Dim okB As Boolean = TryGetProximityKey(b(i), instB, kb)
            If Not okA OrElse Not okB Then
                If Not (Not okA AndAlso Not okB) Then Return False
                Continue For
            End If
            If Not ProximityKeysSimilar(ka, kb, tol) Then Return False
        Next
        Return True
    End Function

    Private Shared Function TryGetInstanceGoo(d As IGH_GeometricGoo) As GH_InstanceReference
        If d Is Nothing Then Return Nothing
        Dim ghInst As GH_InstanceReference = TryCast(d, GH_InstanceReference)
        If ghInst IsNot Nothing Then Return DirectCast(ghInst.Duplicate(), GH_InstanceReference)
        Dim converted As GH_InstanceReference = Nothing
        If GH_Convert.ToGHInstanceReference_Primary(d, converted) AndAlso converted IsNot Nothing Then
            Return DirectCast(converted.Duplicate(), GH_InstanceReference)
        End If
        Dim baseGeo As GeometryBase = Nothing
        Try
            baseGeo = GH_Convert.ToGeometryBase(d)
        Catch
        End Try
        Dim iref As InstanceReferenceGeometry = TryCast(baseGeo, InstanceReferenceGeometry)
        If iref IsNot Nothing Then Return New GH_InstanceReference(iref)
        Return Nothing
    End Function

    Private Shared Function GeometryFromGoo(d As IGH_GeometricGoo) As GeometryBase
        If d Is Nothing Then Return Nothing

        Dim ghInst As GH_InstanceReference = TryGetInstanceGoo(d)
        If ghInst IsNot Nothing Then
            Dim iref As InstanceReferenceGeometry = SelectorInstanceUtil.CreateInstanceReferenceGeometry(ghInst)
            If iref IsNot Nothing Then Return iref
        End If

        Dim baseGeo As GeometryBase = GH_Convert.ToGeometryBase(d)
        If baseGeo Is Nothing Then Return Nothing
        Return baseGeo.Duplicate()
    End Function

    Private Function GooForIndex(i As Integer) As IGH_GeometricGoo
        If i >= 0 AndAlso i < InstanceGoos.Count AndAlso InstanceGoos(i) IsNot Nothing Then
            Return DirectCast(InstanceGoos(i).Duplicate(), IGH_GeometricGoo)
        End If
        Return ToGeometricGoo(Geometries(i))
    End Function

    Private Shared Function ToGeometricGoo(g As GeometryBase) As IGH_GeometricGoo
        If g Is Nothing Then Return Nothing
        Dim iref As InstanceReferenceGeometry = TryCast(g, InstanceReferenceGeometry)
        If iref IsNot Nothing Then Return New GH_InstanceReference(iref)
        Return GH_Convert.ToGeometricGoo(g)
    End Function

    Private Shared Function RepresentativePoint(g As GeometryBase, inst As GH_InstanceReference) As Point3d
        Dim key As GeometryProximityKey = Nothing
        If TryGetProximityKey(g, inst, key) AndAlso key.Center.IsValid Then Return key.Center
        Return Point3d.Unset
    End Function

    ''' <summary>Greedy nearest-geometry matching for selection flags (keeps selection on objects when list order/count changes).</summary>
    Private Shared Function RemapSelectionByProximity(oldGeoms As List(Of GeometryBase), oldInst As List(Of GH_InstanceReference),
                                                        oldSelected As List(Of Boolean),
                                                        oldPaths As List(Of GH_Path), oldBranch As List(Of Integer),
                                                        newGeoms As List(Of GeometryBase), newInst As List(Of GH_InstanceReference),
                                                        newPaths As List(Of GH_Path), newBranch As List(Of Integer)) As List(Of Boolean)
        Const sameIndexTol As Double = 0.0001
        Dim indexTol As Double = Math.Max(sameIndexTol, ModelAbsoluteTolerance())
        Dim result As New List(Of Boolean)(newGeoms.Count)
        For i As Integer = 0 To newGeoms.Count - 1
            result.Add(False)
        Next

        Dim usedOld As New HashSet(Of Integer)
        Dim usedNew As New HashSet(Of Integer)
        Dim nOld As Integer = Math.Min(oldGeoms.Count, oldSelected.Count)

        ' Pass 1: Rhino document instance references match by stable ReferenceID.
        For oi As Integer = 0 To nOld - 1
            If Not oldSelected(oi) OrElse usedOld.Contains(oi) Then Continue For
            Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso oi < oldInst.Count, oldInst(oi), Nothing)
            Dim oldRefId As Guid = Guid.Empty
            If oldInstGoo IsNot Nothing Then
                Try
                    oldRefId = oldInstGoo.ReferenceID
                Catch
                End Try
            End If
            If oldRefId = Guid.Empty Then Continue For

            For ni As Integer = 0 To newGeoms.Count - 1
                If usedNew.Contains(ni) Then Continue For
                Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso ni < newInst.Count, newInst(ni), Nothing)
                If newInstGoo Is Nothing Then Continue For
                Dim newRefId As Guid = Guid.Empty
                Try
                    newRefId = newInstGoo.ReferenceID
                Catch
                End Try
                If newRefId = oldRefId Then
                    result(ni) = True
                    usedOld.Add(oi)
                    usedNew.Add(ni)
                    Exit For
                End If
            Next
        Next

        ' Pass 2: same flat index when path, branch index, and geometry still match (minor upstream jitter).
        Dim sameIndexLimit As Integer = Math.Min(nOld, newGeoms.Count)
        For i As Integer = 0 To sameIndexLimit - 1
            If Not oldSelected(i) OrElse usedOld.Contains(i) OrElse usedNew.Contains(i) Then Continue For
            If oldPaths IsNot Nothing AndAlso newPaths IsNot Nothing AndAlso i < oldPaths.Count AndAlso i < newPaths.Count Then
                If Not PathsEqual(oldPaths(i), newPaths(i)) Then Continue For
            End If
            If oldBranch IsNot Nothing AndAlso newBranch IsNot Nothing AndAlso i < oldBranch.Count AndAlso i < newBranch.Count Then
                If oldBranch(i) <> newBranch(i) Then Continue For
            End If
            Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso i < oldInst.Count, oldInst(i), Nothing)
            Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso i < newInst.Count, newInst(i), Nothing)
            Dim ka As GeometryProximityKey = Nothing
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(i), oldInstGoo, ka) Then Continue For
            If Not TryGetProximityKey(newGeoms(i), newInstGoo, kb) Then Continue For
            If ProximityKeysSimilar(ka, kb, indexTol) Then
                result(i) = True
                usedOld.Add(i)
                usedNew.Add(i)
            End If
        Next

        ' Pass 3: greedy nearest match for remaining selected items (list reorder, insert, remove).
        Dim pairs As New List(Of Tuple(Of Double, Integer, Integer))
        For oi As Integer = 0 To nOld - 1
            If Not oldSelected(oi) OrElse usedOld.Contains(oi) Then Continue For
            Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso oi < oldInst.Count, oldInst(oi), Nothing)
            Dim ka As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(oi), oldInstGoo, ka) Then Continue For
            For ni As Integer = 0 To newGeoms.Count - 1
                If usedNew.Contains(ni) Then Continue For
                Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso ni < newInst.Count, newInst(ni), Nothing)
                Dim kb As GeometryProximityKey = Nothing
                If Not TryGetProximityKey(newGeoms(ni), newInstGoo, kb) Then Continue For
                Dim score As Double = ProximityMatchScore(ka, kb)
                If Double.IsPositiveInfinity(score) Then Continue For
                If Not IsWithinProximityMatch(ka, kb) Then Continue For
                pairs.Add(Tuple.Create(score, oi, ni))
            Next
        Next
        pairs.Sort(Function(x, y) x.Item1.CompareTo(y.Item1))

        For Each p As Tuple(Of Double, Integer, Integer) In pairs
            If usedOld.Contains(p.Item2) OrElse usedNew.Contains(p.Item3) Then Continue For
            Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso p.Item2 < oldInst.Count, oldInst(p.Item2), Nothing)
            Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso p.Item3 < newInst.Count, newInst(p.Item3), Nothing)
            Dim ka As GeometryProximityKey = Nothing
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(p.Item2), oldInstGoo, ka) Then Continue For
            If Not TryGetProximityKey(newGeoms(p.Item3), newInstGoo, kb) Then Continue For
            If Not IsWithinProximityMatch(ka, kb) Then Continue For
            result(p.Item3) = True
            usedOld.Add(p.Item2)
            usedNew.Add(p.Item3)
        Next
        Return result
    End Function

    Private Shared Function ShiftedKeyMatchesCandidate(saved As GeometryProximityKey, candidate As GeometryProximityKey) As Boolean
        If saved.ReferenceId <> Guid.Empty AndAlso candidate.ReferenceId <> Guid.Empty Then
            Return saved.ReferenceId = candidate.ReferenceId
        End If
        Return IsWithinProximityMatch(saved, candidate)
    End Function

    Private Shared Function CloneShiftedKeyList(src As List(Of GeometryProximityKey)) As List(Of GeometryProximityKey)
        If src Is Nothing Then Return New List(Of GeometryProximityKey)
        Return New List(Of GeometryProximityKey)(src)
    End Function

    Private Sub AddShiftedKey(key As GeometryProximityKey)
        For Each existing As GeometryProximityKey In ShiftedSelectionKeys
            If ShiftedKeyMatchesCandidate(existing, key) Then Return
        Next
        ShiftedSelectionKeys.Add(key)
    End Sub

    Private Sub RemoveShiftedKeysMatching(key As GeometryProximityKey)
        ShiftedSelectionKeys.RemoveAll(Function(k) ShiftedKeyMatchesCandidate(k, key))
    End Sub

    Private Shared Function OldItemStillInList(oldGeoms As List(Of GeometryBase), oldInst As List(Of GH_InstanceReference), oi As Integer,
                                               newGeoms As List(Of GeometryBase), newInst As List(Of GH_InstanceReference)) As Boolean
        Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso oi < oldInst.Count, oldInst(oi), Nothing)
        Dim ka As GeometryProximityKey = Nothing
        If Not TryGetProximityKey(oldGeoms(oi), oldInstGoo, ka) Then Return False

        If ka.ReferenceId <> Guid.Empty Then
            For ni As Integer = 0 To newGeoms.Count - 1
                Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso ni < newInst.Count, newInst(ni), Nothing)
                If newInstGoo Is Nothing Then Continue For
                Try
                    If newInstGoo.ReferenceID = ka.ReferenceId Then Return True
                Catch
                End Try
            Next
            Return False
        End If

        For ni As Integer = 0 To newGeoms.Count - 1
            Dim newInstGoo As GH_InstanceReference = If(newInst IsNot Nothing AndAlso ni < newInst.Count, newInst(ni), Nothing)
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(newGeoms(ni), newInstGoo, kb) Then Continue For
            If IsWithinProximityMatch(ka, kb) Then Return True
        Next
        Return False
    End Function

    Private Sub RememberShiftedSelections(oldGeoms As List(Of GeometryBase), oldInst As List(Of GH_InstanceReference),
                                          prevSelected As List(Of Boolean),
                                          newGeoms As List(Of GeometryBase), newInst As List(Of GH_InstanceReference))
        Dim nOld As Integer = Math.Min(oldGeoms.Count, prevSelected.Count)
        For oi As Integer = 0 To nOld - 1
            If Not prevSelected(oi) Then Continue For
            Dim oldInstGoo As GH_InstanceReference = If(oldInst IsNot Nothing AndAlso oi < oldInst.Count, oldInst(oi), Nothing)
            Dim ka As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(oi), oldInstGoo, ka) Then Continue For

            If OldItemStillInList(oldGeoms, oldInst, oi, newGeoms, newInst) Then
                RemoveShiftedKeysMatching(ka)
            Else
                AddShiftedKey(ka)
            End If
        Next
    End Sub

    Private Sub ApplyShiftedSelections(geoms As List(Of GeometryBase), inst As List(Of GH_InstanceReference), selected As List(Of Boolean))
        If ShiftedSelectionKeys.Count = 0 Then Return

        Dim usedSaved As New HashSet(Of Integer)
        For ni As Integer = 0 To geoms.Count - 1
            If ni < selected.Count AndAlso selected(ni) Then Continue For
            Dim newInstGoo As GH_InstanceReference = If(inst IsNot Nothing AndAlso ni < inst.Count, inst(ni), Nothing)
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(geoms(ni), newInstGoo, kb) Then Continue For

            For si As Integer = 0 To ShiftedSelectionKeys.Count - 1
                If usedSaved.Contains(si) Then Continue For
                If Not ShiftedKeyMatchesCandidate(ShiftedSelectionKeys(si), kb) Then Continue For
                While selected.Count <= ni
                    selected.Add(False)
                End While
                selected(ni) = True
                usedSaved.Add(si)
                Exit For
            Next
        Next

        If usedSaved.Count > 0 Then
            Dim remaining As New List(Of GeometryProximityKey)
            For si As Integer = 0 To ShiftedSelectionKeys.Count - 1
                If Not usedSaved.Contains(si) Then remaining.Add(ShiftedSelectionKeys(si))
            Next
            ShiftedSelectionKeys = remaining
        End If
    End Sub

    Private Shared Sub BuildGeometriesFromTree(data As GH_Structure(Of IGH_GeometricGoo), geoms As List(Of GeometryBase), paths As List(Of GH_Path), branchIndices As List(Of Integer), instanceGoos As List(Of GH_InstanceReference))
        geoms.Clear()
        paths.Clear()
        branchIndices.Clear()
        instanceGoos.Clear()
        For Each path As GH_Path In data.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = data.DataList(path)
            For j As Integer = 0 To branch.Count - 1
                geoms.Add(GeometryFromGoo(branch(j)))
                instanceGoos.Add(TryGetInstanceGoo(branch(j)))
                paths.Add(path)
                branchIndices.Add(j)
            Next
        Next
    End Sub

    Private Sub SetOutputTrees(DA As IGH_DataAccess, data As GH_Structure(Of IGH_GeometricGoo))
        Dim selIdx As New GH_Structure(Of GH_Integer)
        Dim selGeom As New GH_Structure(Of IGH_GeometricGoo)

        While Selected.Count < Geometries.Count
            Selected.Add(False)
        End While

        Dim flat As Integer = 0
        For Each path As GH_Path In data.Paths
            Dim branch As IList(Of IGH_GeometricGoo) = data.DataList(path)
            For branchIndex As Integer = 0 To branch.Count - 1
                Dim sel As Boolean = flat < Selected.Count AndAlso Selected(flat)
                If OutputNulls Then
                    If sel Then
                        selIdx.Append(New GH_Integer(branchIndex), path)
                        Dim hasGeom As Boolean = flat < Geometries.Count AndAlso Geometries(flat) IsNot Nothing
                        Dim hasInst As Boolean = flat < InstanceGoos.Count AndAlso InstanceGoos(flat) IsNot Nothing
                        If hasGeom OrElse hasInst Then selGeom.Append(GooForIndex(flat), path)
                    Else
                        selIdx.Append(Nothing, path)
                        selGeom.Append(Nothing, path)
                    End If
                ElseIf sel Then
                    Dim hasGeom As Boolean = flat < Geometries.Count AndAlso Geometries(flat) IsNot Nothing
                    Dim hasInst As Boolean = flat < InstanceGoos.Count AndAlso InstanceGoos(flat) IsNot Nothing
                    If hasGeom OrElse hasInst Then
                        selIdx.Append(New GH_Integer(branchIndex), path)
                        selGeom.Append(GooForIndex(flat), path)
                    End If
                End If
                flat += 1
            Next
        Next

        DA.SetDataTree(0, selIdx)
        DA.SetDataTree(1, selGeom)

        If OutputSplitLists AndAlso Params.Output.Count >= 4 Then
            Dim unselIdx As New GH_Structure(Of GH_Integer)
            Dim unselGeom As New GH_Structure(Of IGH_GeometricGoo)
            flat = 0
            For Each path As GH_Path In data.Paths
                Dim branch As IList(Of IGH_GeometricGoo) = data.DataList(path)
                For branchIndex As Integer = 0 To branch.Count - 1
                    Dim sel As Boolean = flat < Selected.Count AndAlso Selected(flat)
                    If OutputNulls Then
                        If sel Then
                            unselIdx.Append(Nothing, path)
                            unselGeom.Append(Nothing, path)
                        Else
                            Dim hasGeom As Boolean = flat < Geometries.Count AndAlso Geometries(flat) IsNot Nothing
                            Dim hasInst As Boolean = flat < InstanceGoos.Count AndAlso InstanceGoos(flat) IsNot Nothing
                            unselIdx.Append(New GH_Integer(branchIndex), path)
                            If hasGeom OrElse hasInst Then unselGeom.Append(GooForIndex(flat), path)
                        End If
                    ElseIf Not sel Then
                        Dim hasGeom As Boolean = flat < Geometries.Count AndAlso Geometries(flat) IsNot Nothing
                        Dim hasInst As Boolean = flat < InstanceGoos.Count AndAlso InstanceGoos(flat) IsNot Nothing
                        If hasGeom OrElse hasInst Then
                            unselIdx.Append(New GH_Integer(branchIndex), path)
                            unselGeom.Append(GooForIndex(flat), path)
                        End If
                    End If
                    flat += 1
                Next
            Next
            DA.SetDataTree(2, unselIdx)
            DA.SetDataTree(3, unselGeom)
        End If
    End Sub

    Protected Overrides Sub SolveInstance(DA As IGH_DataAccess)
        Dim data As New GH_Structure(Of IGH_GeometricGoo)
        If Not DA.GetDataTree(0, data) Then
            Geometries.Clear()
            ItemPaths.Clear()
            ItemBranchIndices.Clear()
            InstanceGoos.Clear()
            CacheGeometries = Nothing
            CacheInstanceGoos = Nothing
            CacheItemPaths = Nothing
            CacheItemBranchIndices = Nothing
            ShiftedSelectionKeys.Clear()
            SyncMouse()
            Exit Sub
        End If

        Dim newGeoms As New List(Of GeometryBase)
        Dim newPaths As New List(Of GH_Path)
        Dim newBranchIndices As New List(Of Integer)
        BuildGeometriesFromTree(data, newGeoms, newPaths, newBranchIndices, InstanceGoos)

        Dim hadGeometryCache As Boolean = (CacheGeometries IsNot Nothing)
        Dim geometryChanged As Boolean = False

        If CacheGeometries Is Nothing Then
            CacheGeometries = CloneGeometryList(newGeoms)
            CacheInstanceGoos = CloneInstanceGooList(InstanceGoos)
            CacheItemPaths = New List(Of GH_Path)(newPaths)
            CacheItemBranchIndices = New List(Of Integer)(newBranchIndices)
            If ProximityCache AndAlso SaveShifted Then
                ApplyShiftedSelections(newGeoms, InstanceGoos, Selected)
            End If
        ElseIf Not GeometriesEqual(CacheGeometries, newGeoms, CacheInstanceGoos, InstanceGoos, CacheItemPaths, newPaths, CacheItemBranchIndices, newBranchIndices) Then
            geometryChanged = True
            If ProximityCache Then
                Dim prevSelected As New List(Of Boolean)(Selected)
                Selected = RemapSelectionByProximity(CacheGeometries, CacheInstanceGoos, Selected, CacheItemPaths, CacheItemBranchIndices,
                                                      newGeoms, InstanceGoos, newPaths, newBranchIndices)
                If SaveShifted Then
                    RememberShiftedSelections(CacheGeometries, CacheInstanceGoos, prevSelected, newGeoms, InstanceGoos)
                    ApplyShiftedSelections(newGeoms, InstanceGoos, Selected)
                End If
            ElseIf Not PreserveChanges Then
                Selected.Clear()
            End If
            CacheGeometries = CloneGeometryList(newGeoms)
            CacheInstanceGoos = CloneInstanceGooList(InstanceGoos)
            CacheItemPaths = New List(Of GH_Path)(newPaths)
            CacheItemBranchIndices = New List(Of Integer)(newBranchIndices)
        ElseIf ProximityCache AndAlso SaveShifted Then
            ApplyShiftedSelections(newGeoms, InstanceGoos, Selected)
        End If

        Geometries = newGeoms
        ItemPaths = newPaths
        ItemBranchIndices = newBranchIndices

        While Selected.Count < Geometries.Count
            Selected.Add(False)
        End While
        If Not PreserveChanges AndAlso Selected.Count > Geometries.Count Then
            Selected.RemoveRange(Geometries.Count, Selected.Count - Geometries.Count)
        End If

        ApplyPreselectedIndicesIfNeeded(DA, hadGeometryCache, geometryChanged)

        SetOutputTrees(DA, data)
    End Sub

    Private Function FindPreselectInputIndex() As Integer
        If Params Is Nothing Then Return -1
        For i As Integer = 0 To Params.Input.Count - 1
            If Params.Input(i).NickName = "Ix" Then Return i
        Next
        Return -1
    End Function

    Private Shared Function HasAnySelection(flags As List(Of Boolean)) As Boolean
        If flags Is Nothing Then Return False
        For Each sel As Boolean In flags
            If sel Then Return True
        Next
        Return False
    End Function

    Private Shared Function ClonePreselectTree(tree As GH_Structure(Of GH_Integer)) As GH_Structure(Of GH_Integer)
        Dim clone As New GH_Structure(Of GH_Integer)
        If tree Is Nothing Then Return clone
        For Each path As GH_Path In tree.Paths
            For Each gi As GH_Integer In tree.DataList(path)
                If gi Is Nothing Then Continue For
                clone.Append(New GH_Integer(gi.Value), path)
            Next
        Next
        Return clone
    End Function

    Private Function PreselectTreeChanged(newTree As GH_Structure(Of GH_Integer)) As Boolean
        If CachePreselectTree Is Nothing Then Return True
        If newTree Is Nothing Then Return CachePreselectTree.DataCount > 0
        If newTree.PathCount <> CachePreselectTree.PathCount OrElse newTree.DataCount <> CachePreselectTree.DataCount Then Return True
        For Each path As GH_Path In newTree.Paths
            Dim na As IList(Of GH_Integer) = newTree.DataList(path)
            Dim oa As IList(Of GH_Integer) = CachePreselectTree.DataList(path)
            If na Is Nothing OrElse oa Is Nothing OrElse na.Count <> oa.Count Then Return True
            For k As Integer = 0 To na.Count - 1
                Dim nv As Integer = If(TryCast(na(k), GH_Integer)?.Value, Integer.MinValue)
                Dim ov As Integer = If(TryCast(oa(k), GH_Integer)?.Value, Integer.MinValue)
                If nv <> ov Then Return True
            Next
        Next
        For Each path As GH_Path In CachePreselectTree.Paths
            If Not newTree.PathExists(path) Then Return True
        Next
        Return False
    End Function

    Private Sub ApplyPreselectedIndices(preselectData As GH_Structure(Of GH_Integer))
        Dim byPath As New Dictionary(Of GH_Path, HashSet(Of Integer))
        If preselectData IsNot Nothing Then
            For Each path As GH_Path In preselectData.Paths
                Dim hs As New HashSet(Of Integer)
                For Each gi As GH_Integer In preselectData.DataList(path)
                    If gi Is Nothing Then Continue For
                    hs.Add(gi.Value)
                Next
                If hs.Count > 0 Then byPath(path) = hs
            Next
        End If

        While Selected.Count < Geometries.Count
            Selected.Add(False)
        End While

        For i As Integer = 0 To Geometries.Count - 1
            Selected(i) = False
            Dim p As GH_Path = ItemPaths(i)
            Dim j As Integer = ItemBranchIndices(i)
            Dim hs As HashSet(Of Integer) = Nothing
            If byPath.TryGetValue(p, hs) AndAlso hs.Contains(j) Then
                Dim hasGeom As Boolean = Geometries(i) IsNot Nothing
                Dim hasInst As Boolean = i < InstanceGoos.Count AndAlso InstanceGoos(i) IsNot Nothing
                If hasGeom OrElse hasInst Then Selected(i) = True
            End If
        Next
    End Sub

    Private Sub ApplyPreselectedIndicesIfNeeded(DA As IGH_DataAccess, hadGeometryCache As Boolean, geometryChanged As Boolean)
        If Not PreselectedIndices Then Return
        Dim preIx As Integer = FindPreselectInputIndex()
        If preIx < 0 Then Return

        Dim preData As New GH_Structure(Of GH_Integer)
        DA.GetDataTree(preIx, preData)

        Dim treeChanged As Boolean = PreselectTreeChanged(preData)
        Dim resetByGeometry As Boolean = (Not hadGeometryCache) OrElse (geometryChanged AndAlso Not PreserveChanges AndAlso Not ProximityCache)

        If treeChanged Then
            If CachePreselectTree Is Nothing AndAlso HasAnySelection(Selected) Then
                CachePreselectTree = ClonePreselectTree(preData)
            Else
                ApplyPreselectedIndices(preData)
                CachePreselectTree = ClonePreselectTree(preData)
            End If
        ElseIf resetByGeometry Then
            ApplyPreselectedIndices(preData)
            CachePreselectTree = ClonePreselectTree(preData)
        End If
    End Sub

    Private Shared Function CloneGeometryList(src As List(Of GeometryBase)) As List(Of GeometryBase)
        Dim result As New List(Of GeometryBase)(src.Count)
        For Each g As GeometryBase In src
            If g Is Nothing Then
                result.Add(Nothing)
            Else
                result.Add(g.Duplicate())
            End If
        Next
        Return result
    End Function

    Private Shared Function CloneInstanceGooList(src As List(Of GH_InstanceReference)) As List(Of GH_InstanceReference)
        Dim result As New List(Of GH_InstanceReference)(src.Count)
        For Each g As GH_InstanceReference In src
            If g Is Nothing Then
                result.Add(Nothing)
            Else
                result.Add(DirectCast(g.Duplicate(), GH_InstanceReference))
            End If
        Next
        Return result
    End Function

#End Region

#Region "Preview"

    Private Shared Sub DrawGeometryWires(display As DisplayPipeline, geom As GeometryBase, col As Color, thickness As Integer)
        If geom Is Nothing OrElse display Is Nothing Then Return

        Dim iref As InstanceReferenceGeometry = TryCast(geom, InstanceReferenceGeometry)
        If iref IsNot Nothing Then
            SelectorInstanceUtil.ForEachWorldPiece(iref, Nothing, Sub(piece) DrawGeometryWires(display, piece, col, thickness))
            Return
        End If

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

    Private Shared Sub DrawGeometryMeshes(display As DisplayPipeline, geom As GeometryBase, col As Color)
        If geom Is Nothing OrElse display Is Nothing Then Return

        Dim iref As InstanceReferenceGeometry = TryCast(geom, InstanceReferenceGeometry)
        If iref IsNot Nothing Then
            SelectorInstanceUtil.ForEachWorldPiece(iref, Nothing, Sub(piece) DrawGeometryMeshes(display, piece, col))
            Return
        End If

        Dim brep As Brep = TryCast(geom, Brep)
        If brep IsNot Nothing Then
            display.DrawBrepShaded(brep, New DisplayMaterial(col))
            Return
        End If

        Dim ext As Extrusion = TryCast(geom, Extrusion)
        If ext IsNot Nothing Then
            Dim tb As Brep = ext.ToBrep()
            If tb IsNot Nothing Then
                Try
                    display.DrawBrepShaded(tb, New DisplayMaterial(col))
                Finally
                    tb.Dispose()
                End Try
            End If
            Return
        End If

        Dim mesh As Mesh = TryCast(geom, Mesh)
        If mesh IsNot Nothing Then
            display.DrawMeshShaded(mesh, New DisplayMaterial(col))
            Return
        End If

        Dim subd As SubD = TryCast(geom, SubD)
        If subd IsNot Nothing Then
            Dim sm As Mesh = Mesh.CreateFromSubD(subd, 2)
            If sm IsNot Nothing Then
                Try
                    display.DrawMeshShaded(sm, New DisplayMaterial(col))
                Finally
                    sm.Dispose()
                End Try
            End If
        End If
    End Sub

    Public Overrides Sub DrawViewportWires(args As IGH_PreviewArgs)
        If Geometries.Count = 0 Then Return

        Dim selCol As Color = Color.FromArgb(255, 40, 180, 70)
        Dim unselCol As Color = If(Me.Attributes IsNot Nothing AndAlso Me.Attributes.Selected, args.WireColour_Selected, args.WireColour)

        For i As Integer = 0 To Geometries.Count - 1
            Dim g As GeometryBase = Geometries(i)
            Dim ghInst As GH_InstanceReference = If(i < InstanceGoos.Count, InstanceGoos(i), Nothing)
            Dim iref As InstanceReferenceGeometry = TryCast(g, InstanceReferenceGeometry)
            If g Is Nothing AndAlso ghInst IsNot Nothing Then
                iref = SelectorInstanceUtil.CreateInstanceReferenceGeometry(ghInst)
                g = iref
            End If
            If g Is Nothing AndAlso iref Is Nothing AndAlso ghInst Is Nothing Then Continue For
            Dim isSel As Boolean = i < Selected.Count AndAlso Selected(i)
            Dim col As Color = If(isSel, selCol, unselCol)
            Dim thick As Integer = If(isSel, 2, 1)
            If iref IsNot Nothing OrElse ghInst IsNot Nothing Then
                SelectorInstanceUtil.ForEachWorldPiece(iref, ghInst, Sub(piece) DrawGeometryWires(args.Display, piece, col, thick))
            Else
                DrawGeometryWires(args.Display, g, col, thick)
            End If
        Next

        If RectSelectActive Then
            Dim x0 As Integer = Math.Min(RectSelectStart.X, RectSelectEnd.X)
            Dim y0 As Integer = Math.Min(RectSelectStart.Y, RectSelectEnd.Y)
            Dim x1 As Integer = Math.Max(RectSelectStart.X, RectSelectEnd.X)
            Dim y1 As Integer = Math.Max(RectSelectStart.Y, RectSelectEnd.Y)
            If x1 > x0 AndAlso y1 > y0 Then
                Dim windowMode As Boolean = RectSelectStart.X <= RectSelectEnd.X
                Dim border As Color = If(windowMode, Color.FromArgb(160, 96, 128, 192), Color.FromArgb(160, 128, 192, 96))
                Dim fill As Color = Color.FromArgb(10, border.R, border.G, border.B)
                args.Display.Draw2dRectangle(New Rectangle(x0, y0, x1 - x0, y1 - y0), fill, 1, border)
            End If
        End If
    End Sub

    Public Overrides Sub DrawViewportMeshes(args As IGH_PreviewArgs)
        If Geometries.Count = 0 Then Return

        Dim selCol As Color = Color.FromArgb(120, 40, 180, 70)
        For i As Integer = 0 To Geometries.Count - 1
            Dim g As GeometryBase = Geometries(i)
            Dim ghInst As GH_InstanceReference = If(i < InstanceGoos.Count, InstanceGoos(i), Nothing)
            Dim iref As InstanceReferenceGeometry = TryCast(g, InstanceReferenceGeometry)
            If g Is Nothing AndAlso ghInst IsNot Nothing Then
                iref = SelectorInstanceUtil.CreateInstanceReferenceGeometry(ghInst)
                g = iref
            End If
            If g Is Nothing AndAlso iref Is Nothing AndAlso ghInst Is Nothing Then Continue For
            Dim isSel As Boolean = i < Selected.Count AndAlso Selected(i)
            If Not isSel Then Continue For
            If iref IsNot Nothing OrElse ghInst IsNot Nothing Then
                SelectorInstanceUtil.ForEachWorldPiece(iref, ghInst, Sub(piece) DrawGeometryMeshes(args.Display, piece, selCol))
            Else
                DrawGeometryMeshes(args.Display, g, selCol)
            End If
        Next
    End Sub

    Public Overrides ReadOnly Property ClippingBox As BoundingBox
        Get
            Dim bb As BoundingBox = BoundingBox.Empty
            For Each g As GeometryBase In Geometries
                If g Is Nothing Then Continue For
                bb.Union(g.GetBoundingBox(True))
            Next
            Return bb
        End Get
    End Property

#End Region

#Region "Write/Read"

    Public Overrides Function Write(writer As GH_IO.Serialization.GH_IWriter) As Boolean
        writer.SetBoolean("GS_Preserve", PreserveChanges)
        writer.SetBoolean("GS_Proximity", ProximityCache)
        writer.SetBoolean("GS_SaveShifted", SaveShifted)
        writer.SetBoolean("GS_Split", OutputSplitLists)
        writer.SetBoolean("GS_Nulls", OutputNulls)
        writer.SetBoolean("GS_Preselected", PreselectedIndices)
        writer.SetBoolean("GS_LockUnselected", LockUnselected)
        writer.SetInt32("GS_Count", Selected.Count)
        For i As Integer = 0 To Selected.Count - 1
            writer.SetBoolean("GS_Sel", i, Selected(i))
        Next
        writer.SetInt32("GS_ShiftedCount", ShiftedSelectionKeys.Count)
        For i As Integer = 0 To ShiftedSelectionKeys.Count - 1
            Dim key As GeometryProximityKey = ShiftedSelectionKeys(i)
            writer.SetInt32("GS_ShiftType", i, key.ObjectType)
            writer.SetDouble("GS_ShiftCx", i, key.Center.X)
            writer.SetDouble("GS_ShiftCy", i, key.Center.Y)
            writer.SetDouble("GS_ShiftCz", i, key.Center.Z)
            writer.SetDouble("GS_ShiftDiag", i, key.Diagonal)
            writer.SetGuid("GS_ShiftIdef", i, key.InstanceDefId)
            writer.SetGuid("GS_ShiftRef", i, key.ReferenceId)
        Next
        Return MyBase.Write(writer)
    End Function

    Public Overrides Function Read(reader As GH_IO.Serialization.GH_IReader) As Boolean
        Dim preserve As Boolean = True
        reader.TryGetBoolean("GS_Preserve", preserve)
        PreserveChanges = preserve

        Dim prox As Boolean = False
        reader.TryGetBoolean("GS_Proximity", prox)
        ProximityCache = prox

        Dim saveShifted As Boolean = False
        reader.TryGetBoolean("GS_SaveShifted", saveShifted)
        SaveShifted = saveShifted

        Dim split As Boolean = False
        reader.TryGetBoolean("GS_Split", split)
        OutputSplitLists = split

        Dim nulls As Boolean = False
        reader.TryGetBoolean("GS_Nulls", nulls)
        OutputNulls = nulls

        Dim preselected As Boolean = False
        reader.TryGetBoolean("GS_Preselected", preselected)
        PreselectedIndices = preselected

        Dim lockUnsel As Boolean = True
        reader.TryGetBoolean("GS_LockUnselected", lockUnsel)
        LockUnselected = lockUnsel

        Selected.Clear()
        Dim n As Integer = 0
        If reader.TryGetInt32("GS_Count", n) Then
            For i As Integer = 0 To n - 1
                Dim sel As Boolean = False
                reader.TryGetBoolean("GS_Sel", i, sel)
                Selected.Add(sel)
            Next
        End If

        ShiftedSelectionKeys.Clear()
        Dim shiftedCount As Integer = 0
        If reader.TryGetInt32("GS_ShiftedCount", shiftedCount) AndAlso shiftedCount > 0 Then
            For i As Integer = 0 To shiftedCount - 1
                Dim key As New GeometryProximityKey
                reader.TryGetInt32("GS_ShiftType", i, key.ObjectType)
                Dim cx As Double = 0, cy As Double = 0, cz As Double = 0
                reader.TryGetDouble("GS_ShiftCx", i, cx)
                reader.TryGetDouble("GS_ShiftCy", i, cy)
                reader.TryGetDouble("GS_ShiftCz", i, cz)
                key.Center = New Point3d(cx, cy, cz)
                reader.TryGetDouble("GS_ShiftDiag", i, key.Diagonal)
                reader.TryGetGuid("GS_ShiftIdef", i, key.InstanceDefId)
                reader.TryGetGuid("GS_ShiftRef", i, key.ReferenceId)
                If key.Center.IsValid OrElse key.ReferenceId <> Guid.Empty Then
                    ShiftedSelectionKeys.Add(key)
                End If
            Next
        End If

        VariableParameterMaintenance()
        Return MyBase.Read(reader)
    End Function

#End Region

#Region "Variable parameters"

    Public Function CanInsertParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanInsertParameter
        Return False
    End Function

    Public Function CanRemoveParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.CanRemoveParameter
        Return False
    End Function

    Public Function CreateParameter(side As GH_ParameterSide, index As Integer) As IGH_Param Implements IGH_VariableParameterComponent.CreateParameter
        If side = GH_ParameterSide.Input Then
            If index = 1 Then
                Dim p As New Grasshopper.Kernel.Parameters.Param_Integer()
                p.Name = "Preselected indices"
                p.NickName = "Ix"
                p.Description = "Tree of branch-local indices to preselect (paths should match the geometry input). Viewport picking can override."
                p.Access = GH_ParamAccess.tree
                p.Optional = True
                Return p
            End If
            Return Nothing
        End If

        If side <> GH_ParameterSide.Output Then Return Nothing
        Select Case index
            Case 2
                Dim p As New Grasshopper.Kernel.Parameters.Param_Integer()
                p.Name = "Index (unselected)"
                p.NickName = "Iu"
                p.Description = "Index of each unselected item within its input tree branch."
                p.Access = GH_ParamAccess.tree
                Return p
            Case 3
                Dim p As New Grasshopper.Kernel.Parameters.Param_Geometry()
                p.Name = "Geometry (unselected)"
                p.NickName = "Gu"
                p.Description = "Unselected geometry."
                p.Access = GH_ParamAccess.tree
                Return p
        End Select
        Return Nothing
    End Function

    Public Function DestroyParameter(side As GH_ParameterSide, index As Integer) As Boolean Implements IGH_VariableParameterComponent.DestroyParameter
        Return True
    End Function

    Public Sub VariableParameterMaintenance() Implements IGH_VariableParameterComponent.VariableParameterMaintenance
        If Params Is Nothing Then Return

        Dim preIx As Integer = FindPreselectInputIndex()
        If Not PreselectedIndices AndAlso preIx >= 0 Then
            Dim p As IGH_Param = Params.Input(preIx)
            p.RemoveAllSources()
            Params.UnregisterInputParameter(p)
        ElseIf PreselectedIndices AndAlso preIx < 0 Then
            Dim param As IGH_Param = CreateParameter(GH_ParameterSide.Input, 1)
            If param IsNot Nothing Then Params.RegisterInputParam(param, 1)
        End If

        If OutputSplitLists Then
            While Params.Output.Count < 4
                Dim index As Integer = Params.Output.Count
                Dim param As IGH_Param = CreateParameter(GH_ParameterSide.Output, index)
                If param Is Nothing Then Exit While
                Params.RegisterOutputParam(param)
            End While
        Else
            While Params.Output.Count > 2
                Params.UnregisterOutputParameter(Params.Output(Params.Output.Count - 1))
            End While
        End If
    End Sub

#End Region

End Class

Public Class GeometrySelectCompAtt
    Inherits Grasshopper.Kernel.Attributes.GH_ComponentAttributes

    Private ReadOnly MyOwner As GeometrySelectComp

    Sub New(owner As GeometrySelectComp)
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

Public Class GeometrySelectUndo
    Inherits Grasshopper.Kernel.Undo.GH_UndoAction

    Private ReadOnly _ownerId As Guid
    Private _selected As List(Of Boolean)
    Private _preserve As Boolean
    Private _proximity As Boolean
    Private _split As Boolean
    Private _nulls As Boolean
    Private _preselected As Boolean
    Private _lockUnselected As Boolean

    Private _saveShifted As Boolean
    Private _shiftedKeys As List(Of GeometryProximityKey)

    Sub New(owner As GeometrySelectComp)
        _ownerId = owner.InstanceGuid
        _selected = New List(Of Boolean)(owner.Selected)
        _preserve = owner.PreserveChanges
        _proximity = owner.ProximityCache
        _saveShifted = owner.SaveShifted
        _shiftedKeys = CloneShiftedKeysForUndo(owner.ShiftedSelectionKeys)
        _split = owner.OutputSplitLists
        _nulls = owner.OutputNulls
        _preselected = owner.PreselectedIndices
        _lockUnselected = owner.LockUnselected
    End Sub

    Private Shared Function CloneShiftedKeysForUndo(src As List(Of GeometryProximityKey)) As List(Of GeometryProximityKey)
        If src Is Nothing Then Return New List(Of GeometryProximityKey)
        Return New List(Of GeometryProximityKey)(src)
    End Function

    Protected Overrides Sub Internal_Undo(doc As GH_Document)
        Dim comp As GeometrySelectComp = TryCast(doc.FindObject(_ownerId, True), GeometrySelectComp)
        If comp Is Nothing Then Return
        comp.SetSelectionFromUndo(_selected, _preserve, _proximity, _saveShifted, _shiftedKeys, _preselected, _split, _nulls, _lockUnselected)
    End Sub

    Protected Overrides Sub Internal_Redo(doc As GH_Document)
        Dim comp As GeometrySelectComp = TryCast(doc.FindObject(_ownerId, True), GeometrySelectComp)
        If comp Is Nothing Then Return
        Dim curSelected As New List(Of Boolean)(comp.Selected)
        Dim curPreserve As Boolean = comp.PreserveChanges
        Dim curProximity As Boolean = comp.ProximityCache
        Dim curSaveShifted As Boolean = comp.SaveShifted
        Dim curShiftedKeys As List(Of GeometryProximityKey) = CloneShiftedKeysForUndo(comp.ShiftedSelectionKeys)
        Dim curPreselected As Boolean = comp.PreselectedIndices
        Dim curSplit As Boolean = comp.OutputSplitLists
        Dim curNulls As Boolean = comp.OutputNulls
        Dim curLock As Boolean = comp.LockUnselected
        comp.SetSelectionFromUndo(_selected, _preserve, _proximity, _saveShifted, _shiftedKeys, _preselected, _split, _nulls, _lockUnselected)
        _selected = curSelected
        _preserve = curPreserve
        _proximity = curProximity
        _saveShifted = curSaveShifted
        _shiftedKeys = curShiftedKeys
        _preselected = curPreselected
        _split = curSplit
        _nulls = curNulls
        _lockUnselected = curLock
    End Sub

End Class

''' <summary>Viewport clicks and rectangle drags toggle geometry selection while the component is selected on canvas.</summary>
Public Class GeometrySelectMouse
    Inherits Rhino.UI.MouseCallback

    Private ReadOnly Comp As GeometrySelectComp

    Private Const PickRadiusPx As Double = 15.0R
    Private Const ClickSlopPx As Double = 4.0R
    Private Const PickMeshVertexLimit As Integer = 20000

    Private _mouseDown As Boolean
    Private _dragSelecting As Boolean
    Private _pendingHit As Integer = -1
    Private _downViewport As Drawing.Point

    Sub New(owner As GeometrySelectComp)
        Comp = owner
    End Sub

    Protected Overrides Sub OnMouseDown(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseDown(e)
        _mouseDown = False
        _dragSelecting = False
        _pendingHit = -1
        If Comp Is Nothing Then Exit Sub
        If e.Button <> MouseButtons.Left Then Exit Sub
        If e.View Is Nothing Then Exit Sub

        Dim vp As RhinoViewport = e.View.ActiveViewport
        If vp Is Nothing Then Exit Sub

        _mouseDown = True
        _downViewport = e.ViewportPoint
        _pendingHit = PickGeometryIndex(vp, e.ViewportPoint)
        Comp.SetRectSelectState(False, _downViewport, _downViewport)
        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseMove(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseMove(e)
        If Not _mouseDown Then Exit Sub
        If Comp Is Nothing Then Exit Sub

        If Not _dragSelecting Then
            Dim dx As Double = CDbl(e.ViewportPoint.X) - CDbl(_downViewport.X)
            Dim dy As Double = CDbl(e.ViewportPoint.Y) - CDbl(_downViewport.Y)
            If (dx * dx + dy * dy) > (ClickSlopPx * ClickSlopPx) Then
                _dragSelecting = True
                _pendingHit = -1
            End If
        End If

        If _dragSelecting Then
            Comp.SetRectSelectState(True, _downViewport, e.ViewportPoint)
            Try
                Rhino.RhinoDoc.ActiveDoc.Views.Redraw()
            Catch
            End Try
        End If
        e.Cancel = True
    End Sub

    Protected Overrides Sub OnMouseUp(e As Rhino.UI.MouseCallbackEventArgs)
        MyBase.OnMouseUp(e)
        If Comp Is Nothing Then Exit Sub
        If Not _mouseDown Then Exit Sub
        If e.Button <> MouseButtons.Left Then
            _mouseDown = False
            _dragSelecting = False
            Comp.SetRectSelectState(False, Drawing.Point.Empty, Drawing.Point.Empty)
            Exit Sub
        End If

        Dim wasDrag As Boolean = _dragSelecting
        Dim hit As Integer = _pendingHit
        Dim downPt As Drawing.Point = _downViewport
        _mouseDown = False
        _dragSelecting = False
        _pendingHit = -1
        Comp.SetRectSelectState(False, Drawing.Point.Empty, Drawing.Point.Empty)

        If wasDrag Then
            If e.View IsNot Nothing AndAlso e.View.ActiveViewport IsNot Nothing Then
                Dim picks As List(Of Integer) = PickIndicesInRectangle(e.View.ActiveViewport, downPt, e.ViewportPoint)
                If picks.Count > 0 Then
                    Dim mode As GeometrySelectComp.SelectionApplyMode = RectSelectModeFromEvent(e)
                    Comp.ApplySelections(picks, mode)
                End If
            End If
            e.Cancel = True
            Exit Sub
        End If

        If hit >= 0 Then
            Comp.ToggleSelection(hit)
            e.Cancel = True
        End If
    End Sub

    Private Shared Function RectSelectModeFromEvent(e As Rhino.UI.MouseCallbackEventArgs) As GeometrySelectComp.SelectionApplyMode
        Dim subtract As Boolean = e.CtrlKeyDown
        If Not subtract Then
            Try
                subtract = (Control.ModifierKeys And Keys.Control) <> 0
            Catch
            End Try
        End If
        If subtract Then Return GeometrySelectComp.SelectionApplyMode.DeselectOnly

        Dim addOnly As Boolean = e.ShiftKeyDown
        If Not addOnly Then
            Try
                addOnly = (Control.ModifierKeys And Keys.Shift) <> 0
            Catch
            End Try
        End If
        If addOnly Then Return GeometrySelectComp.SelectionApplyMode.SelectOnly
        Return GeometrySelectComp.SelectionApplyMode.Toggle
    End Function

    Private Structure ScreenBounds
        Public Valid As Boolean
        Public MinX As Double
        Public MinY As Double
        Public MaxX As Double
        Public MaxY As Double
    End Structure

    Private Sub IncludeScreenPoint(vp As RhinoViewport, pt As Point3d, ByRef bounds As ScreenBounds)
        If Not pt.IsValid Then Return
        If Not vp.IsVisible(pt) Then Return
        Dim sp As Rhino.Geometry.Point2d = vp.WorldToClient(pt)
        If Not bounds.Valid Then
            bounds.Valid = True
            bounds.MinX = sp.X
            bounds.MaxX = sp.X
            bounds.MinY = sp.Y
            bounds.MaxY = sp.Y
        Else
            bounds.MinX = Math.Min(bounds.MinX, sp.X)
            bounds.MaxX = Math.Max(bounds.MaxX, sp.X)
            bounds.MinY = Math.Min(bounds.MinY, sp.Y)
            bounds.MaxY = Math.Max(bounds.MaxY, sp.Y)
        End If
    End Sub

    Private Sub IncludeWorldGeometryScreenBounds(vp As RhinoViewport, geom As GeometryBase, ByRef bounds As ScreenBounds)
        If geom Is Nothing Then Return

        Dim pt As Rhino.Geometry.Point = TryCast(geom, Rhino.Geometry.Point)
        If pt IsNot Nothing Then
            IncludeScreenPoint(vp, pt.Location, bounds)
            Return
        End If

        Dim crv As Curve = TryCast(geom, Curve)
        If crv IsNot Nothing Then
            Const samples As Integer = 24
            For si As Integer = 0 To samples
                Dim t As Double = crv.Domain.ParameterAt(CDbl(si) / CDbl(samples))
                IncludeScreenPoint(vp, crv.PointAt(t), bounds)
            Next
            Return
        End If

        Dim bb As BoundingBox = geom.GetBoundingBox(True)
        If bb.IsValid Then
            For Each c As Point3d In bb.GetCorners()
                IncludeScreenPoint(vp, c, bounds)
            Next
        End If
    End Sub

    Private Function GetItemScreenBounds(index As Integer, vp As RhinoViewport) As ScreenBounds
        Dim bounds As New ScreenBounds With {.Valid = False}
        If Comp Is Nothing OrElse index < 0 OrElse index >= Comp.Geometries.Count Then Return bounds

        Dim geom As GeometryBase = Comp.Geometries(index)
        Dim ghInst As GH_InstanceReference = If(index < Comp.InstanceGoos.Count, Comp.InstanceGoos(index), Nothing)
        Dim iref As InstanceReferenceGeometry = TryCast(geom, InstanceReferenceGeometry)
        If geom Is Nothing AndAlso ghInst IsNot Nothing Then
            iref = SelectorInstanceUtil.CreateInstanceReferenceGeometry(ghInst)
            geom = iref
        End If
        If geom Is Nothing AndAlso iref Is Nothing AndAlso ghInst Is Nothing Then Return bounds

        If iref IsNot Nothing OrElse ghInst IsNot Nothing Then
            SelectorInstanceUtil.ForEachWorldPiece(iref, ghInst, Sub(piece) IncludeWorldGeometryScreenBounds(vp, piece, bounds))
            If Not bounds.Valid Then
                Dim worldBb As BoundingBox = SelectorInstanceUtil.GetInstanceWorldBoundingBox(iref, ghInst)
                If worldBb.IsValid Then
                    For Each c As Point3d In worldBb.GetCorners()
                        IncludeScreenPoint(vp, c, bounds)
                    Next
                End If
            End If
        Else
            IncludeWorldGeometryScreenBounds(vp, geom, bounds)
        End If
        Return bounds
    End Function

    Private Shared Function ScreenRectsIntersect(a As ScreenBounds, pickMinX As Double, pickMinY As Double, pickMaxX As Double, pickMaxY As Double) As Boolean
        Return Not (a.MaxX < pickMinX OrElse a.MinX > pickMaxX OrElse a.MaxY < pickMinY OrElse a.MinY > pickMaxY)
    End Function

    Private Shared Function ScreenRectFullyInside(a As ScreenBounds, pickMinX As Double, pickMinY As Double, pickMaxX As Double, pickMaxY As Double) As Boolean
        Return a.MinX >= pickMinX AndAlso a.MaxX <= pickMaxX AndAlso a.MinY >= pickMinY AndAlso a.MaxY <= pickMaxY
    End Function

    Private Function PickIndicesInRectangle(vp As RhinoViewport, downPt As Drawing.Point, upPt As Drawing.Point) As List(Of Integer)
        Dim result As New List(Of Integer)
        If Comp Is Nothing OrElse vp Is Nothing Then Return result

        Dim pickMinX As Double = Math.Min(CDbl(downPt.X), CDbl(upPt.X))
        Dim pickMaxX As Double = Math.Max(CDbl(downPt.X), CDbl(upPt.X))
        Dim pickMinY As Double = Math.Min(CDbl(downPt.Y), CDbl(upPt.Y))
        Dim pickMaxY As Double = Math.Max(CDbl(downPt.Y), CDbl(upPt.Y))
        If pickMaxX - pickMinX < 1.0R AndAlso pickMaxY - pickMinY < 1.0R Then Return result

        Dim windowMode As Boolean = downPt.X <= upPt.X

        For i As Integer = 0 To Comp.Geometries.Count - 1
            Dim bounds As ScreenBounds = GetItemScreenBounds(i, vp)
            If Not bounds.Valid Then Continue For
            Dim include As Boolean =
                If(windowMode,
                   ScreenRectFullyInside(bounds, pickMinX, pickMinY, pickMaxX, pickMaxY),
                   ScreenRectsIntersect(bounds, pickMinX, pickMinY, pickMaxX, pickMaxY))
            If include Then result.Add(i)
        Next
        Return result
    End Function

    Private Structure PickCandidate
        Public Q As Point3d
        Public Rank As Integer
    End Structure

    Private Function PickGeometryIndex(vp As RhinoViewport, viewportPoint As Drawing.Point) As Integer
        Dim ray As Line = Nothing
        If Not vp.GetFrustumLine(CDbl(viewportPoint.X), CDbl(viewportPoint.Y), ray) Then Return -1
        If Not ray.IsValid Then Return -1

        Dim cursor As New Rhino.Geometry.Point2d(CDbl(viewportPoint.X), CDbl(viewportPoint.Y))
        Dim cameraLocation As Point3d = ray.To
        Try
            Dim camPt As Point3d = vp.CameraLocation
            If camPt.IsValid Then cameraLocation = camPt
        Catch
        End Try

        Dim bestIx As Integer = -1
        Dim bestRank As Integer = Integer.MaxValue
        Dim bestMetric As Double = Double.PositiveInfinity
        Dim pickRadiusSq As Double = PickRadiusPx * PickRadiusPx

        For i As Integer = 0 To Comp.Geometries.Count - 1
            Dim geom As GeometryBase = Comp.Geometries(i)
            Dim ghInst As GH_InstanceReference = If(i < Comp.InstanceGoos.Count, Comp.InstanceGoos(i), Nothing)
            Dim iref As InstanceReferenceGeometry = TryCast(geom, InstanceReferenceGeometry)
            If geom Is Nothing AndAlso ghInst IsNot Nothing Then
                iref = SelectorInstanceUtil.CreateInstanceReferenceGeometry(ghInst)
                geom = iref
            End If
            If geom Is Nothing AndAlso iref Is Nothing AndAlso ghInst Is Nothing Then Continue For

            Dim cands As New List(Of PickCandidate)
            If iref IsNot Nothing OrElse ghInst IsNot Nothing Then
                CollectInstancePickCandidates(iref, ghInst, ray, vp, cursor, cands)
            Else
                CollectPickCandidates(geom, ray, cands)
            End If
            If cands.Count = 0 Then Continue For

            For Each cand As PickCandidate In cands
                Dim q As Point3d = cand.Q
                If Not q.IsValid Then Continue For
                If Not vp.IsVisible(q) Then Continue For

                Dim sq As Rhino.Geometry.Point2d = vp.WorldToClient(q)
                Dim dPxSq As Double = (sq.X - cursor.X) * (sq.X - cursor.X) + (sq.Y - cursor.Y) * (sq.Y - cursor.Y)
                If dPxSq > pickRadiusSq Then Continue For

                Dim metric As Double
                If cand.Rank >= 2 Then
                    metric = q.DistanceToSquared(cameraLocation)
                Else
                    metric = dPxSq
                End If

                If cand.Rank < bestRank OrElse (cand.Rank = bestRank AndAlso metric < bestMetric) Then
                    bestRank = cand.Rank
                    bestMetric = metric
                    bestIx = i
                End If
            Next
        Next

        Return bestIx
    End Function

    Private Shared Sub CollectPickCandidates(geom As GeometryBase, ray As Line, cands As List(Of PickCandidate))
        If geom Is Nothing Then Return
        Try
            If Not geom.IsValid Then Return
        Catch
            Return
        End Try

        Try
            Dim pt As Rhino.Geometry.Point = TryCast(geom, Rhino.Geometry.Point)
            If pt IsNot Nothing Then
                If pt.Location.IsValid Then cands.Add(New PickCandidate With {.Q = pt.Location, .Rank = 0})
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
                If best.IsValid Then cands.Add(New PickCandidate With {.Q = best, .Rank = 0})
                Return
            End If

            Dim crv As Curve = TryCast(geom, Curve)
            If crv IsNot Nothing Then
                CollectCurvePickCandidates(crv, ray, cands)
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
                    CollectBrepPickCandidates(brep, ray, cands)
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
                    CollectMeshPickCandidates(mesh, ray, cands)
                Finally
                    If ownedMesh IsNot Nothing Then ownedMesh.Dispose()
                End Try
                Return
            End If

            Dim q As Point3d
            If TrySampledClosestToRay(geom, ray, q) Then
                cands.Add(New PickCandidate With {.Q = q, .Rank = 2})
            End If
        Catch
        End Try
    End Sub

    Private Shared Sub CollectInstancePickCandidates(iref As InstanceReferenceGeometry, ghInst As GH_InstanceReference, ray As Line, vp As RhinoViewport, cursor As Rhino.Geometry.Point2d, cands As List(Of PickCandidate))
        Dim beforeCount As Integer = cands.Count
        SelectorInstanceUtil.ForEachWorldPiece(iref, ghInst, Sub(piece) CollectPickCandidates(piece, ray, cands))

        Dim worldBb As BoundingBox = SelectorInstanceUtil.GetInstanceWorldBoundingBox(iref, ghInst)
        If worldBb.IsValid Then
            Dim tol As Double = 0.001
            Try
                tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
            Catch
            End Try
            Dim tRange As Interval = Interval.Unset
            If Rhino.Geometry.Intersect.Intersection.LineBox(ray, worldBb, tol, tRange) AndAlso tRange.IsValid Then
                Dim hitPt As Point3d = ray.PointAt(tRange.T0)
                If hitPt.IsValid Then
                    Dim sq As Rhino.Geometry.Point2d = vp.WorldToClient(hitPt)
                    Dim dPxSq As Double = (sq.X - cursor.X) * (sq.X - cursor.X) + (sq.Y - cursor.Y) * (sq.Y - cursor.Y)
                    If dPxSq <= (PickRadiusPx * PickRadiusPx) Then
                        cands.Add(New PickCandidate With {.Q = hitPt, .Rank = 1})
                    End If
                End If
            End If
            AppendBoundingBoxCandidates(worldBb, ray, cands)
        ElseIf cands.Count = beforeCount Then
            Dim bb As BoundingBox = BoundingBox.Empty
            If iref IsNot Nothing Then
                bb = iref.GetBoundingBox(True)
            ElseIf ghInst IsNot Nothing Then
                Try
                    bb = ghInst.Boundingbox
                Catch
                End Try
            End If
            AppendBoundingBoxCandidates(bb, ray, cands)
        End If
    End Sub

    Private Shared Sub AppendBoundingBoxCandidates(bb As BoundingBox, ray As Line, cands As List(Of PickCandidate))
        If Not bb.IsValid Then Return
        For Each c As Point3d In bb.GetCorners()
            If c.IsValid Then cands.Add(New PickCandidate With {.Q = c, .Rank = 2})
        Next
        Dim center As Point3d = bb.Center
        If center.IsValid Then cands.Add(New PickCandidate With {.Q = center, .Rank = 2})
    End Sub

    Private Shared Sub CollectCurvePickCandidates(crv As Curve, ray As Line, cands As List(Of PickCandidate))
        If Not crv.IsClosed Then
            If crv.PointAtStart.IsValid Then cands.Add(New PickCandidate With {.Q = crv.PointAtStart, .Rank = 0})
            If crv.PointAtEnd.IsValid Then cands.Add(New PickCandidate With {.Q = crv.PointAtEnd, .Rank = 0})
        End If
        Dim ptCrv As Point3d = Nothing
        Dim ptRay As Point3d = Nothing
        Using lc As New LineCurve(ray)
            If crv.ClosestPoints(lc, ptCrv, ptRay) AndAlso ptCrv.IsValid Then
                cands.Add(New PickCandidate With {.Q = ptCrv, .Rank = 1})
                Return
            End If
        End Using
        Dim q As Point3d
        If TrySampledClosestToRay(crv, ray, q) Then
            cands.Add(New PickCandidate With {.Q = q, .Rank = 1})
        End If
    End Sub

    Private Shared Sub CollectBrepPickCandidates(brep As Brep, ray As Line, cands As List(Of PickCandidate))
        For Each v As BrepVertex In brep.Vertices
            If v.Location.IsValid Then cands.Add(New PickCandidate With {.Q = v.Location, .Rank = 0})
        Next

        For Each edge As BrepEdge In brep.Edges
            Dim ptCrv As Point3d = Nothing
            Dim ptRay As Point3d = Nothing
            Using lc As New LineCurve(ray)
                If edge.ClosestPoints(lc, ptCrv, ptRay) AndAlso ptCrv.IsValid Then
                    cands.Add(New PickCandidate With {.Q = ptCrv, .Rank = 1})
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
                        cands.Add(New PickCandidate With {.Q = h, .Rank = 2})
                        anyHit = True
                    End If
                Next
            End If
        End Using
        If Not anyHit Then
            Dim q As Point3d
            If TrySampledClosestToRay(brep, ray, q) Then
                cands.Add(New PickCandidate With {.Q = q, .Rank = 2})
            End If
        End If
    End Sub

    Private Shared Sub CollectMeshPickCandidates(mesh As Mesh, ray As Line, cands As List(Of PickCandidate))
        If mesh.Vertices.Count <= PickMeshVertexLimit Then
            Dim best As Point3d = Point3d.Unset
            Dim bestD2 As Double = Double.PositiveInfinity
            For vi As Integer = 0 To mesh.Vertices.Count - 1
                Dim vp As Point3d = mesh.Vertices(vi)
                Dim d2 As Double = vp.DistanceToSquared(ray.ClosestPoint(vp, False))
                If d2 < bestD2 Then
                    bestD2 = d2
                    best = vp
                End If
            Next
            If best.IsValid Then cands.Add(New PickCandidate With {.Q = best, .Rank = 0})
        End If

        Dim hits As Point3d() = Rhino.Geometry.Intersect.Intersection.MeshLine(mesh, ray)
        Dim anyHit As Boolean = False
        If hits IsNot Nothing Then
            For Each h As Point3d In hits
                If h.IsValid Then
                    cands.Add(New PickCandidate With {.Q = h, .Rank = 2})
                    anyHit = True
                End If
            Next
        End If
        If Not anyHit Then
            Dim q As Point3d
            If TrySampledClosestToRay(mesh, ray, q) Then
                cands.Add(New PickCandidate With {.Q = q, .Rank = 2})
            End If
        End If
    End Sub

    Private Shared Function TrySampledClosestToRay(geom As GeometryBase, ray As Line, ByRef q As Point3d) As Boolean
        Const samples As Integer = 48
        Dim best As Point3d = Point3d.Unset
        Dim bestD2 As Double = Double.PositiveInfinity
        For si As Integer = 0 To samples
            Dim p As Point3d = ray.PointAt(CDbl(si) / CDbl(samples))
            Dim cq As Point3d
            If Not TryClosestPointOnGeometry(geom, p, cq) Then Continue For
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
            If Not TryClosestPointOnGeometry(geom, rp, refined) Then Exit For
            best = refined
        Next
        q = best
        Return q.IsValid
    End Function

    Private Shared Function TryClosestPointOnGeometry(geom As GeometryBase, p As Point3d, ByRef q As Point3d) As Boolean
        If geom Is Nothing Then Return False
        Try
            If Not geom.IsValid Then Return False
        Catch
            Return False
        End Try

        Try
            Dim iref As InstanceReferenceGeometry = TryCast(geom, InstanceReferenceGeometry)
            If iref IsNot Nothing Then
                Dim best As Point3d = Point3d.Unset
                Dim bestD2 As Double = Double.PositiveInfinity
                SelectorInstanceUtil.ForEachWorldPiece(iref, Nothing, Sub(piece)
                                                         Dim cq As Point3d
                                                         If TryClosestPointOnGeometry(piece, p, cq) AndAlso cq.IsValid Then
                                                             Dim d2 As Double = p.DistanceToSquared(cq)
                                                             If d2 < bestD2 Then
                                                                 bestD2 = d2
                                                                 best = cq
                                                             End If
                                                         End If
                                                     End Sub)
                If best.IsValid Then
                    q = best
                    Return True
                End If
                Return False
            End If

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
                Return TryClosestBrepPoint(brep, p, q)
            End If

            Dim ext As Extrusion = TryCast(geom, Extrusion)
            If ext IsNot Nothing Then
                Dim tb As Brep = ext.ToBrep()
                If tb Is Nothing Then Return False
                Try
                    Return TryClosestBrepPoint(tb, p, q)
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

    Private Shared Function TryClosestBrepPoint(brep As Brep, p As Point3d, ByRef q As Point3d) As Boolean
        Dim cpt As New Point3d
        Dim ci As ComponentIndex
        Dim nv As Vector3d
        If brep.ClosestPoint(p, cpt, ci, Nothing, Nothing, 0, nv) AndAlso cpt.IsValid Then
            q = cpt
            Return True
        End If
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

End Class
