Imports Grasshopper.Kernel.Data
Imports Rhino.Geometry

''' <summary>Shared proximity fingerprint matching (Selector / Text Tag / Gumball).</summary>
Friend Module ProximityMatching

    Friend Function TryGetProximityKey(g As GeometryBase, ByRef key As GeometryProximityKey) As Boolean
        key = New GeometryProximityKey With {
            .ObjectType = 0,
            .Center = Point3d.Unset,
            .Diagonal = 0.0R,
            .InstanceDefId = Guid.Empty,
            .ReferenceId = Guid.Empty
        }
        If g Is Nothing Then Return False
        key.ObjectType = CInt(g.ObjectType)
        Dim bb As BoundingBox = g.GetBoundingBox(True)
        If Not bb.IsValid Then Return False
        key.Center = bb.Center
        key.Diagonal = bb.Diagonal.Length
        Return key.Center.IsValid
    End Function

    Friend Function ModelAbsoluteTolerance() As Double
        Try
            Dim doc As Rhino.RhinoDoc = Rhino.RhinoDoc.ActiveDoc
            If doc IsNot Nothing Then Return doc.ModelAbsoluteTolerance
        Catch
        End Try
        Return 0.001
    End Function

  Friend Function ProximityKeysSimilar(a As GeometryProximityKey, b As GeometryProximityKey, tol As Double) As Boolean
        If a.ObjectType <> b.ObjectType Then Return False
        If a.ReferenceId <> Guid.Empty AndAlso b.ReferenceId <> Guid.Empty Then Return a.ReferenceId = b.ReferenceId
        If a.InstanceDefId <> Guid.Empty AndAlso b.InstanceDefId <> Guid.Empty AndAlso a.InstanceDefId <> b.InstanceDefId Then Return False
        If Not a.Center.IsValid OrElse Not b.Center.IsValid Then Return False
        If a.Center.DistanceTo(b.Center) > tol Then Return False
        If Math.Abs(a.Diagonal - b.Diagonal) > tol Then Return False
        Return True
    End Function

    Friend Function MaxProximityMatchDistance(a As GeometryProximityKey, b As GeometryProximityKey) As Double
        Dim docTol As Double = ModelAbsoluteTolerance()
        Dim minDiag As Double = Math.Min(Math.Max(a.Diagonal, docTol), Math.Max(b.Diagonal, docTol))
        Return Math.Max(docTol * 10.0R, minDiag * 0.5R)
    End Function

    Friend Function IsWithinProximityMatch(a As GeometryProximityKey, b As GeometryProximityKey) As Boolean
        If Not a.Center.IsValid OrElse Not b.Center.IsValid Then Return False
        If a.Center.DistanceTo(b.Center) > MaxProximityMatchDistance(a, b) Then Return False
        Dim maxDiag As Double = Math.Max(a.Diagonal, b.Diagonal)
        Dim docTol As Double = ModelAbsoluteTolerance()
        If maxDiag > docTol AndAlso Math.Abs(a.Diagonal - b.Diagonal) > maxDiag * 0.5R Then Return False
        Return True
    End Function

    Friend Function ProximityMatchScore(oldKey As GeometryProximityKey, newKey As GeometryProximityKey) As Double
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

    Friend Function ShiftedKeyMatchesCandidate(saved As GeometryProximityKey, candidate As GeometryProximityKey) As Boolean
        If saved.ReferenceId <> Guid.Empty AndAlso candidate.ReferenceId <> Guid.Empty Then
            Return saved.ReferenceId = candidate.ReferenceId
        End If
        Return IsWithinProximityMatch(saved, candidate)
    End Function

    Private Function PathsEqual(a As GH_Path, b As GH_Path) As Boolean
        If a Is Nothing AndAlso b Is Nothing Then Return True
        If a Is Nothing OrElse b Is Nothing Then Return False
        Return a.Equals(b)
    End Function

    ''' <summary>Maps each new slot index to an old slot index (-1 = no match). Uses same-index then greedy nearest within tolerance.</summary>
    Friend Function BuildTransformSlotMap(oldGeoms As GeometryBase(), newGeoms As GeometryBase(),
                                          oldPaths As IList(Of GH_Path), oldBranch As IList(Of Integer),
                                          newPaths As IList(Of GH_Path), newBranch As IList(Of Integer)) As Integer()
        Dim nNew As Integer = If(newGeoms Is Nothing, 0, newGeoms.Length)
        Dim map As Integer() = Enumerable.Repeat(-1, Math.Max(0, nNew)).ToArray()
        If oldGeoms Is Nothing OrElse newGeoms Is Nothing OrElse nNew = 0 OrElse oldGeoms.Length = 0 Then Return map

        Dim nOld As Integer = oldGeoms.Length
        Dim usedOld As New HashSet(Of Integer)
        Dim usedNew As New HashSet(Of Integer)
        Const sameIndexTol As Double = 0.0001
        Dim indexTol As Double = Math.Max(sameIndexTol, ModelAbsoluteTolerance())

        Dim sameIndexLimit As Integer = Math.Min(nOld, nNew)
        For i As Integer = 0 To sameIndexLimit - 1
            If usedOld.Contains(i) OrElse usedNew.Contains(i) Then Continue For
            If oldPaths IsNot Nothing AndAlso newPaths IsNot Nothing AndAlso i < oldPaths.Count AndAlso i < newPaths.Count Then
                If Not PathsEqual(oldPaths(i), newPaths(i)) Then Continue For
            End If
            If oldBranch IsNot Nothing AndAlso newBranch IsNot Nothing AndAlso i < oldBranch.Count AndAlso i < newBranch.Count Then
                If oldBranch(i) <> newBranch(i) Then Continue For
            End If
            Dim ka As GeometryProximityKey = Nothing
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(i), ka) Then Continue For
            If Not TryGetProximityKey(newGeoms(i), kb) Then Continue For
            If ProximityKeysSimilar(ka, kb, indexTol) Then
                map(i) = i
                usedOld.Add(i)
                usedNew.Add(i)
            End If
        Next

        Dim pairs As New List(Of Tuple(Of Double, Integer, Integer))
        For oi As Integer = 0 To nOld - 1
            If usedOld.Contains(oi) Then Continue For
            Dim ka As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(oi), ka) Then Continue For
            For ni As Integer = 0 To nNew - 1
                If usedNew.Contains(ni) Then Continue For
                Dim kb As GeometryProximityKey = Nothing
                If Not TryGetProximityKey(newGeoms(ni), kb) Then Continue For
                Dim score As Double = ProximityMatchScore(ka, kb)
                If Double.IsPositiveInfinity(score) Then Continue For
                If Not IsWithinProximityMatch(ka, kb) Then Continue For
                pairs.Add(Tuple.Create(score, oi, ni))
            Next
        Next
        pairs.Sort(Function(x, y) x.Item1.CompareTo(y.Item1))

        For Each p As Tuple(Of Double, Integer, Integer) In pairs
            If usedOld.Contains(p.Item2) OrElse usedNew.Contains(p.Item3) Then Continue For
            Dim ka As GeometryProximityKey = Nothing
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(oldGeoms(p.Item2), ka) Then Continue For
            If Not TryGetProximityKey(newGeoms(p.Item3), kb) Then Continue For
            If Not IsWithinProximityMatch(ka, kb) Then Continue For
            map(p.Item3) = p.Item2
            usedOld.Add(p.Item2)
            usedNew.Add(p.Item3)
        Next

        Return map
    End Function

    Friend Function OldGeometryStillInList(oldGeoms As GeometryBase(), oi As Integer, newGeoms As GeometryBase()) As Boolean
        If oldGeoms Is Nothing OrElse newGeoms Is Nothing OrElse oi < 0 OrElse oi >= oldGeoms.Length Then Return False
        Dim ka As GeometryProximityKey = Nothing
        If Not TryGetProximityKey(oldGeoms(oi), ka) Then Return False
        For ni As Integer = 0 To newGeoms.Length - 1
            Dim kb As GeometryProximityKey = Nothing
            If Not TryGetProximityKey(newGeoms(ni), kb) Then Continue For
            If IsWithinProximityMatch(ka, kb) Then Return True
        Next
        Return False
    End Function

End Module
