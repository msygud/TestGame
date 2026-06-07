using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Unit
{
    public enum SelectionMarkerShape
    {
        Ring,
        CornerRectangle,
    }

    public sealed class UnitSelectionTestController : MonoBehaviour
    {
        [Header("Input")]
        public Camera SelectionCamera;
        public LayerMask GroundLayerMask = ~0;
        public float GroundPlaneY = 0f;
        public float DragThresholdPixels = 8f;
        public float WorldPickDistance = 2f;

        [Header("Move Order")]
        public float MoveStopDistance = 0.25f;
        public float FormationSpacing = 1.4f;
        public float FormationPadding = 0.35f;
        public bool AvoidOccupiedSlots = true;
        [Range(0, 8)]
        public int SlotSearchRings = 4;

        [Header("Selection Ring")]
        public SelectionMarkerShape MarkerShape = SelectionMarkerShape.CornerRectangle;
        public Material RingMaterial;
        public Color RingColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        public float RingHeightOffset = 0.04f;
        [Range(0.03f, 0.5f)]
        public float RingWidthRatio = 0.14f;
        [Range(16, 128)]
        public int RingSegments = 64;
        [Range(0.05f, 0.5f)]
        public float CornerLengthRatio = 0.28f;
        [Range(0.01f, 0.2f)]
        public float CornerWidthRatio = 0.05f;

        [Header("Debug")]
        public bool ShowDebugHud = true;
        public bool LogSelectionEvents;

        EntityManager _entityManager;
        EntityQuery _unitQuery;
        EntityQuery _selectedQuery;

        bool _hasWorld;
        bool _isDragging;
        Vector2 _dragStart;
        Vector2 _dragEnd;

        Mesh _ringMesh;
        Mesh _cornerRectangleMesh;
        Material _runtimeRingMaterial;
        readonly Dictionary<Entity, GameObject> _rings = new();
        readonly List<Entity> _removeBuffer = new();
        readonly List<MoveSlotReservation> _moveSlotReservations = new();

        struct MoveSlotReservation
        {
            public float3 Position;
            public float Radius;
        }

        void Awake()
        {
            if (SelectionCamera == null)
                SelectionCamera = Camera.main;
        }

        void OnDisable()
        {
            ClearRingObjects();
        }

        void OnDestroy()
        {
            ClearRingObjects();

            if (_ringMesh != null)
                Destroy(_ringMesh);

            if (_cornerRectangleMesh != null)
                Destroy(_cornerRectangleMesh);

            if (_runtimeRingMaterial != null)
                Destroy(_runtimeRingMaterial);
        }

        void Update()
        {
            if (!EnsureWorld())
                return;

            if (SelectionCamera == null)
                SelectionCamera = Camera.main;

            var mouse = Mouse.current;
            if (mouse == null || SelectionCamera == null)
            {
                UpdateSelectionRings();
                return;
            }

            Vector2 mousePosition = mouse.position.ReadValue();
            bool additive = IsAdditiveSelectionPressed();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _dragStart = mousePosition;
                _dragEnd = mousePosition;
            }

            if (_isDragging && mouse.leftButton.isPressed)
                _dragEnd = mousePosition;

            if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
            {
                _isDragging = false;
                _dragEnd = mousePosition;

                if (Vector2.Distance(_dragStart, _dragEnd) >= DragThresholdPixels)
                    SelectUnitsInScreenRect(BuildScreenRect(_dragStart, _dragEnd), additive);
                else
                    SelectUnitAtScreenPoint(mousePosition, additive);
            }

            if (mouse.rightButton.wasPressedThisFrame && TryGetGroundPoint(mousePosition, out var target))
                IssueMoveOrders(target);

            UpdateSelectionRings();
        }

        void OnGUI()
        {
            if (ShowDebugHud && _hasWorld)
            {
                var oldColor = GUI.color;
                GUI.color = Color.white;
                GUI.Label(
                    new Rect(12f, 12f, 360f, 48f),
                    $"Units: {_unitQuery.CalculateEntityCount()}  Selected: {_selectedQuery.CalculateEntityCount()}");
                GUI.color = oldColor;
            }

            if (!_isDragging)
                return;

            var rect = BuildGuiRect(_dragStart, _dragEnd);
            var oldDragColor = GUI.color;

            GUI.color = new Color(0.2f, 0.75f, 1f, 0.18f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            GUI.color = new Color(0.2f, 0.9f, 1f, 0.75f);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMax, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, 1f, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax, rect.yMin, 1f, rect.height), Texture2D.whiteTexture);

            GUI.color = oldDragColor;
        }

        bool EnsureWorld()
        {
            if (_hasWorld)
                return true;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;

            _entityManager = world.EntityManager;
            _unitQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<SelectableUnit>(),
                ComponentType.ReadOnly<UnitFootprint>(),
                ComponentType.ReadOnly<UnitActivityState>(),
                ComponentType.ReadOnly<UnitSelectionRadius>(),
                ComponentType.ReadOnly<LocalTransform>());
            _selectedQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<SelectedUnit>(),
                ComponentType.ReadOnly<UnitFootprint>(),
                ComponentType.ReadOnly<UnitActivityState>(),
                ComponentType.ReadOnly<UnitSelectionRadius>(),
                ComponentType.ReadOnly<LocalTransform>());

            _hasWorld = true;
            return true;
        }

        void SelectUnitAtScreenPoint(Vector2 screenPoint, bool additive)
        {
            Entity picked = FindUnitAtScreenPoint(screenPoint);
            if (picked == Entity.Null && TryGetGroundPoint(screenPoint, out var worldPoint))
                picked = FindUnitNearWorldPoint(worldPoint);

            if (!additive)
                ClearSelection();

            if (picked == Entity.Null)
            {
                if (LogSelectionEvents)
                    Debug.Log("[UnitSelectionTestController] No unit picked.");
                return;
            }

            if (additive)
            {
                bool selected = _entityManager.IsComponentEnabled<SelectedUnit>(picked);
                _entityManager.SetComponentEnabled<SelectedUnit>(picked, !selected);
            }
            else
            {
                _entityManager.SetComponentEnabled<SelectedUnit>(picked, true);
            }

            if (LogSelectionEvents)
                Debug.Log($"[UnitSelectionTestController] Picked unit {picked}.");
        }

        Entity FindUnitAtScreenPoint(Vector2 screenPoint)
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _unitQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                Vector3 screen = SelectionCamera.WorldToScreenPoint(ToVector3(transforms[i].Position));
                if (screen.z <= 0f)
                    continue;

                float2 delta = new float2(screen.x - screenPoint.x, screen.y - screenPoint.y);
                float distanceSq = math.lengthsq(delta);
                float pickRadius = radii[i].ScreenPixels;

                if (distanceSq <= pickRadius * pickRadius && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();

            return best;
        }

        Entity FindUnitNearWorldPoint(float3 worldPoint)
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _unitQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);
            var footprints = _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                float3 delta = transforms[i].Position - worldPoint;
                delta.y = 0f;

                float pickDistance = math.max(
                    WorldPickDistance,
                    math.max(radii[i].WorldRadius, math.max(radii[i].RingRadius, footprints[i].Radius)));
                float distanceSq = math.lengthsq(delta);

                if (distanceSq <= pickDistance * pickDistance && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();
            footprints.Dispose();

            return best;
        }

        void SelectUnitsInScreenRect(Rect rect, bool additive)
        {
            if (!additive)
                ClearSelection();

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Vector3 screen = SelectionCamera.WorldToScreenPoint(ToVector3(transforms[i].Position));
                if (screen.z > 0f && rect.Contains(new Vector2(screen.x, screen.y)))
                    _entityManager.SetComponentEnabled<SelectedUnit>(entities[i], true);
            }

            entities.Dispose();
            transforms.Dispose();

            if (LogSelectionEvents)
                Debug.Log($"[UnitSelectionTestController] Drag selected. Selected={_selectedQuery.CalculateEntityCount()}.");
        }

        void ClearSelection()
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (_entityManager.HasComponent<SelectedUnit>(entities[i]))
                    _entityManager.SetComponentEnabled<SelectedUnit>(entities[i], false);
            }

            entities.Dispose();
        }

        void IssueMoveOrders(float3 target)
        {
            var selected = _selectedQuery.ToEntityArray(Allocator.Temp);
            if (selected.Length == 0)
            {
                selected.Dispose();
                return;
            }

            var transforms = _selectedQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var footprints = _selectedQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);
            var allUnits = _unitQuery.ToEntityArray(Allocator.Temp);
            var allTransforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var allFootprints = _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);
            var allActivities = _unitQuery.ToComponentDataArray<UnitActivityState>(Allocator.Temp);
            float3 selectionCenter = CalculateSelectionCenter(transforms);
            float3 forward = target - selectionCenter;
            forward.y = 0f;

            if (math.lengthsq(forward) <= 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            float3 right = math.normalize(math.cross(math.up(), forward));
            int columns = Mathf.CeilToInt(Mathf.Sqrt(selected.Length));
            int rows = Mathf.CeilToInt(selected.Length / (float)columns);
            var columnWidths = new NativeArray<float>(columns, Allocator.Temp);
            var rowDepths = new NativeArray<float>(rows, Allocator.Temp);

            CalculateFormationBounds(footprints, columns, columnWidths, rowDepths);
            _moveSlotReservations.Clear();

            for (int i = 0; i < selected.Length; i++)
            {
                int x = i % columns;
                int z = i / columns;

                float offsetX = GetSlotCenterOffset(columnWidths, x);
                float offsetZ = GetSlotCenterOffset(rowDepths, z);
                float3 desiredDestination = target + right * offsetX + forward * offsetZ;
                float3 destination = AvoidOccupiedSlots
                    ? FindAvailableMoveSlot(
                        desiredDestination,
                        footprints[i],
                        right,
                        forward,
                        allUnits,
                        allTransforms,
                        allFootprints,
                        allActivities)
                    : desiredDestination;

                _moveSlotReservations.Add(new MoveSlotReservation
                {
                    Position = destination,
                    Radius = math.max(0.01f, footprints[i].Radius),
                });

                Entity request = _entityManager.CreateEntity(typeof(MoveOrderRequest));
                _entityManager.SetComponentData(request, new MoveOrderRequest
                {
                    Unit = selected[i],
                    Target = destination,
                    StopDistance = MoveStopDistance,
                });
            }

            allActivities.Dispose();
            allFootprints.Dispose();
            allTransforms.Dispose();
            allUnits.Dispose();
            rowDepths.Dispose();
            columnWidths.Dispose();
            footprints.Dispose();
            transforms.Dispose();
            selected.Dispose();
        }

        float3 CalculateSelectionCenter(NativeArray<LocalTransform> transforms)
        {
            float3 center = float3.zero;

            for (int i = 0; i < transforms.Length; i++)
                center += transforms[i].Position;

            return center / math.max(1, transforms.Length);
        }

        void CalculateFormationBounds(
            NativeArray<UnitFootprint> footprints,
            int columns,
            NativeArray<float> columnWidths,
            NativeArray<float> rowDepths)
        {
            float fallback = math.max(0.1f, FormationSpacing);
            float padding = math.max(0f, FormationPadding);

            for (int i = 0; i < footprints.Length; i++)
            {
                int x = i % columns;
                int z = i / columns;
                UnitFootprint footprint = footprints[i];

                float2 size = math.max(footprint.Size, new float2(footprint.Radius * 2f));
                float width = math.max(fallback, size.x + padding);
                float depth = math.max(fallback, size.y + padding);

                columnWidths[x] = math.max(columnWidths[x], width);
                rowDepths[z] = math.max(rowDepths[z], depth);
            }
        }

        static float GetSlotCenterOffset(NativeArray<float> sizes, int index)
        {
            float total = 0f;
            for (int i = 0; i < sizes.Length; i++)
                total += sizes[i];

            float offset = -total * 0.5f;
            for (int i = 0; i < index; i++)
                offset += sizes[i];

            return offset + sizes[index] * 0.5f;
        }

        float3 FindAvailableMoveSlot(
            float3 desiredDestination,
            UnitFootprint footprint,
            float3 right,
            float3 forward,
            NativeArray<Entity> allUnits,
            NativeArray<LocalTransform> allTransforms,
            NativeArray<UnitFootprint> allFootprints,
            NativeArray<UnitActivityState> allActivities)
        {
            float radius = math.max(0.01f, footprint.Radius);
            float searchStep = math.max(FormationSpacing, radius * 2f + math.max(0f, FormationPadding));

            if (IsMoveSlotAvailable(desiredDestination, radius, allUnits, allTransforms, allFootprints, allActivities))
                return desiredDestination;

            for (int ring = 1; ring <= SlotSearchRings; ring++)
            {
                for (int z = -ring; z <= ring; z++)
                {
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (math.max(math.abs(x), math.abs(z)) != ring)
                            continue;

                        float3 candidate = desiredDestination + right * (x * searchStep) + forward * (z * searchStep);
                        if (IsMoveSlotAvailable(candidate, radius, allUnits, allTransforms, allFootprints, allActivities))
                            return candidate;
                    }
                }
            }

            return desiredDestination;
        }

        bool IsMoveSlotAvailable(
            float3 destination,
            float radius,
            NativeArray<Entity> allUnits,
            NativeArray<LocalTransform> allTransforms,
            NativeArray<UnitFootprint> allFootprints,
            NativeArray<UnitActivityState> allActivities)
        {
            for (int i = 0; i < _moveSlotReservations.Count; i++)
            {
                float minDistance = radius + _moveSlotReservations[i].Radius + math.max(0f, FormationPadding);
                if (HorizontalDistanceSq(destination, _moveSlotReservations[i].Position) < minDistance * minDistance)
                    return false;
            }

            for (int i = 0; i < allUnits.Length; i++)
            {
                if (_entityManager.IsComponentEnabled<SelectedUnit>(allUnits[i]))
                    continue;

                float otherRadius = math.max(0.01f, allFootprints[i].Radius);
                float minDistance = radius + otherRadius + GetOccupiedSlotPadding(allActivities[i]);
                if (HorizontalDistanceSq(destination, allTransforms[i].Position) < minDistance * minDistance)
                    return false;
            }

            return true;
        }

        float GetOccupiedSlotPadding(UnitActivityState activity)
        {
            float padding = math.max(0f, FormationPadding);

            switch (activity.Value)
            {
                case UnitActivityKind.Working:
                    return padding * 2f;
                case UnitActivityKind.Anchored:
                    return padding * 3f;
                default:
                    return padding;
            }
        }

        static float HorizontalDistanceSq(float3 a, float3 b)
        {
            float3 delta = a - b;
            delta.y = 0f;
            return math.lengthsq(delta);
        }

        bool TryGetGroundPoint(Vector2 screenPoint, out float3 point)
        {
            Ray ray = SelectionCamera.ScreenPointToRay(screenPoint);

            if (Physics.Raycast(ray, out var hit, 5000f, GroundLayerMask))
            {
                point = ToFloat3(hit.point);
                return true;
            }

            var plane = new Plane(Vector3.up, new Vector3(0f, GroundPlaneY, 0f));
            if (plane.Raycast(ray, out float enter))
            {
                point = ToFloat3(ray.GetPoint(enter));
                return true;
            }

            point = float3.zero;
            return false;
        }

        void UpdateSelectionRings()
        {
            if (!_hasWorld)
                return;

            EnsureRingAssets();

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _unitQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);
            var footprints = _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);

            _removeBuffer.Clear();
            foreach (var pair in _rings)
            {
                bool stillSelected = false;
                for (int i = 0; i < entities.Length; i++)
                {
                    if (pair.Key == entities[i] && _entityManager.IsComponentEnabled<SelectedUnit>(entities[i]))
                    {
                        stillSelected = true;
                        break;
                    }
                }

                if (!stillSelected)
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                if (_rings.TryGetValue(_removeBuffer[i], out var ring))
                    Destroy(ring);

                _rings.Remove(_removeBuffer[i]);
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (!_entityManager.IsComponentEnabled<SelectedUnit>(entities[i]))
                    continue;

                if (!_rings.TryGetValue(entities[i], out var ring))
                {
                    ring = CreateRingObject();
                    _rings.Add(entities[i], ring);
                }

                float radius = math.max(0.01f, radii[i].RingRadius);
                float2 footprintSize = math.max(footprints[i].Size, new float2(0.01f, 0.01f));
                float3 position = transforms[i].Position;
                ring.transform.position = new Vector3(position.x, position.y + RingHeightOffset, position.z);
                ring.transform.rotation = ToQuaternion(transforms[i].Rotation);

                var filter = ring.GetComponent<MeshFilter>();
                if (MarkerShape == SelectionMarkerShape.CornerRectangle)
                {
                    filter.sharedMesh = _cornerRectangleMesh;
                    ring.transform.localScale = new Vector3(footprintSize.x, 1f, footprintSize.y);
                }
                else
                {
                    filter.sharedMesh = _ringMesh;
                    ring.transform.localScale = new Vector3(radius, 1f, radius);
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();
            footprints.Dispose();
        }

        GameObject CreateRingObject()
        {
            var ring = new GameObject("Selection Ring");
            ring.transform.SetParent(transform, false);

            var filter = ring.AddComponent<MeshFilter>();
            filter.sharedMesh = _ringMesh;

            var renderer = ring.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _runtimeRingMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return ring;
        }

        void EnsureRingAssets()
        {
            if (_ringMesh == null)
                _ringMesh = BuildRingMesh(math.max(16, RingSegments), math.clamp(RingWidthRatio, 0.03f, 0.5f));

            if (_cornerRectangleMesh == null)
                _cornerRectangleMesh = BuildCornerRectangleMesh(
                    math.clamp(CornerLengthRatio, 0.05f, 0.5f),
                    math.clamp(CornerWidthRatio, 0.01f, 0.2f));

            if (_runtimeRingMaterial != null)
                return;

            if (RingMaterial != null)
            {
                _runtimeRingMaterial = new Material(RingMaterial);
                _runtimeRingMaterial.name = "Runtime Selection Ring";
                ApplyRingMaterialSettings(_runtimeRingMaterial, RingColor);
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            _runtimeRingMaterial = new Material(shader);
            _runtimeRingMaterial.name = "Runtime Selection Ring";
            ApplyRingMaterialSettings(_runtimeRingMaterial, RingColor);
        }

        static Mesh BuildRingMesh(int segments, float widthRatio)
        {
            float inner = math.max(0.01f, 1f - widthRatio);
            var vertices = new Vector3[(segments + 1) * 2];
            var triangles = new int[segments * 12];

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * math.PI * 2f;
                float x = math.cos(angle);
                float z = math.sin(angle);

                vertices[i * 2] = new Vector3(x, 0f, z);
                vertices[i * 2 + 1] = new Vector3(x * inner, 0f, z * inner);
            }

            for (int i = 0; i < segments; i++)
            {
                int v = i * 2;
                int tri = i * 12;

                triangles[tri] = v;
                triangles[tri + 1] = v + 1;
                triangles[tri + 2] = v + 2;
                triangles[tri + 3] = v + 1;
                triangles[tri + 4] = v + 3;
                triangles[tri + 5] = v + 2;

                triangles[tri + 6] = v;
                triangles[tri + 7] = v + 2;
                triangles[tri + 8] = v + 1;
                triangles[tri + 9] = v + 1;
                triangles[tri + 10] = v + 2;
                triangles[tri + 11] = v + 3;
            }

            var mesh = new Mesh
            {
                name = "Selection Ring Mesh",
                vertices = vertices,
                triangles = triangles,
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static Mesh BuildCornerRectangleMesh(float lengthRatio, float widthRatio)
        {
            var vertices = new List<Vector3>(64);
            var triangles = new List<int>(96);

            float half = 0.5f;
            float length = math.min(lengthRatio, 0.5f);
            float width = math.min(widthRatio, length * 0.5f);

            AddCorner(vertices, triangles, -half, half, length, width, true, true);
            AddCorner(vertices, triangles, half, half, length, width, false, true);
            AddCorner(vertices, triangles, -half, -half, length, width, true, false);
            AddCorner(vertices, triangles, half, -half, length, width, false, false);

            var mesh = new Mesh
            {
                name = "Selection Corner Rectangle Mesh",
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray(),
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AddCorner(
            List<Vector3> vertices,
            List<int> triangles,
            float cornerX,
            float cornerZ,
            float length,
            float width,
            bool left,
            bool top)
        {
            float x0 = left ? cornerX : cornerX - length;
            float x1 = left ? cornerX + length : cornerX;
            float zOuter0 = top ? cornerZ - width : cornerZ;
            float zOuter1 = top ? cornerZ : cornerZ + width;
            AddQuad(vertices, triangles, x0, zOuter0, x1, zOuter1);

            float xInner0 = left ? cornerX : cornerX - width;
            float xInner1 = left ? cornerX + width : cornerX;
            float z0 = top ? cornerZ - length : cornerZ;
            float z1 = top ? cornerZ : cornerZ + length;
            AddQuad(vertices, triangles, xInner0, z0, xInner1, z1);
        }

        static void AddQuad(List<Vector3> vertices, List<int> triangles, float x0, float z0, float x1, float z1)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(x0, 0f, z0));
            vertices.Add(new Vector3(x0, 0f, z1));
            vertices.Add(new Vector3(x1, 0f, z1));
            vertices.Add(new Vector3(x1, 0f, z0));

            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);

            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start);
            triangles.Add(start + 3);
            triangles.Add(start + 2);
        }

        static void ApplyRingMaterialSettings(Material material, Color color)
        {
            material.color = color;

            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_Cull"))
                material.SetFloat("_Cull", 0f);
        }

        void ClearRingObjects()
        {
            foreach (var pair in _rings)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _rings.Clear();
        }

        static bool IsAdditiveSelectionPressed()
        {
            var keyboard = Keyboard.current;
            return keyboard != null &&
                   (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        }

        static Rect BuildScreenRect(Vector2 a, Vector2 b)
        {
            float xMin = math.min(a.x, b.x);
            float yMin = math.min(a.y, b.y);
            float xMax = math.max(a.x, b.x);
            float yMax = math.max(a.y, b.y);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static Rect BuildGuiRect(Vector2 a, Vector2 b)
        {
            float yA = Screen.height - a.y;
            float yB = Screen.height - b.y;
            float xMin = math.min(a.x, b.x);
            float yMin = math.min(yA, yB);
            float xMax = math.max(a.x, b.x);
            float yMax = math.max(yA, yB);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static Vector3 ToVector3(float3 value)
            => new Vector3(value.x, value.y, value.z);

        static float3 ToFloat3(Vector3 value)
            => new float3(value.x, value.y, value.z);

        static Quaternion ToQuaternion(quaternion value)
            => new Quaternion(value.value.x, value.value.y, value.value.z, value.value.w);
    }
}
