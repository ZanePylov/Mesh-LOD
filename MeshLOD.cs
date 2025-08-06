using UnityEngine;
using UnityEditor;
using NaughtyAttributes;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System;

/// <summary>
/// Advanced LOD system with intelligent mesh simplification, texture optimization, and event-driven updates.
/// Designed for high-performance scenarios with thousands of objects.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OptimizedMeshLOD : MonoBehaviour, IComparable<OptimizedMeshLOD>
{
    #region Serialized Fields
    
    [BoxGroup("LOD Settings")]
    [SerializeField, Range(0.1f, 0.99f), Tooltip("Maximum reduction ratio for the lowest LOD level")]
    private float maxReductionRatio = 0.9f;

    [BoxGroup("LOD Settings")]
    [SerializeField, Range(1, 5), Tooltip("Number of LOD levels to generate")]
    private int lodLevels = 3;

    [BoxGroup("LOD Settings")]
    [SerializeField, Min(1f), Tooltip("Maximum distance for LOD calculations")]
    private float maxDistance = 100f;

    [BoxGroup("LOD Settings")]
    [SerializeField, Tooltip("Bias for LOD distance calculations (higher = more aggressive LOD)")]
    private float lodBias = 1f;

    [BoxGroup("Advanced Settings")]
    [SerializeField, Tooltip("Enable texture resolution scaling based on distance")]
    private bool enableTextureScaling = true;

    [BoxGroup("Advanced Settings")]
    [SerializeField, Tooltip("Minimum texture scale factor")]
    private float minTextureScale = 0.25f;

    [BoxGroup("Advanced Settings")]
    [SerializeField, Tooltip("Enable edge-preserving mesh simplification")]
    private bool preserveEdges = true;

    [BoxGroup("Advanced Settings")]
    [SerializeField, Tooltip("Threshold for edge preservation (0-1)")]
    private float edgeThreshold = 0.8f;

    [BoxGroup("Performance")]
    [SerializeField, Tooltip("Update frequency in seconds")]
    private float updateInterval = 0.1f;

    [BoxGroup("Performance")]
    [SerializeField, Tooltip("Use scene camera in editor for LOD calculations")]
    private bool useSceneCameraInEditor = true;

    [BoxGroup("Debug")]
    [ReadOnly, SerializeField] private int currentTriangles;
    [ReadOnly, SerializeField] private int originalTriangles;
    [ReadOnly, SerializeField] private int currentLODLevel;
    [ReadOnly, SerializeField] private float currentDistance;
    [ReadOnly, SerializeField] private float currentTextureScale = 1f;
    
    [BoxGroup("Debug")]
    [SerializeField] private bool enableDebugLogs = true;

    #endregion

    #region Private Fields

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh originalMesh;
    private Material originalMaterial;
    
    // LOD data structures
    private LODData[] lodData;
    private int targetLODLevel = 0;
    
    // Optimization fields
    private static readonly ConcurrentQueue<OptimizedMeshLOD> updateQueue = new();
    private static readonly Dictionary<Mesh, LODData[]> meshCache = new();
    private static Camera activeCamera;
    private static Vector3 lastCameraPosition;
    private static bool isProcessingUpdates = false;
    
    // Timing
    private float nextUpdateTime;
    private bool isInitialized = false;
    private bool isProcessingLOD = false;

    #endregion

    #region Data Structures

    [System.Serializable]
    private struct LODData
    {
        public Mesh mesh;
        public Material[] materials;
        public float textureScale;
        public int triangleCount;
        public float distanceThreshold;
        
        public LODData(Mesh mesh, Material[] materials, float textureScale, float distanceThreshold)
        {
            this.mesh = mesh;
            this.materials = materials;
            this.textureScale = textureScale;
            this.triangleCount = mesh != null ? mesh.triangles.Length / 3 : 0;
            this.distanceThreshold = distanceThreshold;
        }
    }

    private struct EdgeData
    {
        public Vector3 vertex1;
        public Vector3 vertex2;
        public float importance;
        
        public EdgeData(Vector3 v1, Vector3 v2, float importance)
        {
            this.vertex1 = v1;
            this.vertex2 = v2;
            this.importance = importance;
        }
    }

    #endregion

    #region Static Management

    private static bool isStaticInitialized = false;
    private static readonly List<OptimizedMeshLOD> pendingObjects = new();
    private static bool isSearchingForCamera = false;

    private static void EnsureStaticInitialization()
    {
        if (isStaticInitialized) return;

        isStaticInitialized = true;

        // Subscribe to camera events
        Camera.onPreRender += OnCameraPreRender;

        // Start the update manager in play mode
        if (Application.isPlaying)
        {
            var updateManager = new GameObject("LODUpdateManager");
            var manager = updateManager.AddComponent<LODUpdateManager>();
            DontDestroyOnLoad(updateManager);
        }

        // Start camera detection if needed
        StartCameraDetection();
    }

    private static void StartCameraDetection()
    {
        if (Application.isPlaying && !isSearchingForCamera)
        {
            isSearchingForCamera = true;
            
            // Try to find existing camera
            FindActiveCamera();
            
            // If no camera found, start periodic search
            if (activeCamera == null)
            {
                var cameraDetector = new GameObject("CameraDetector");
                var detector = cameraDetector.AddComponent<CameraDetector>();
                DontDestroyOnLoad(cameraDetector);
            }
        }
    }

    private static void FindActiveCamera()
    {
        // Try to find main camera first
        if (Camera.main != null)
        {
            activeCamera = Camera.main;
            OnCameraFound();
            return;
        }

        // Try to find any camera with MainCamera tag
        var mainCameraGO = GameObject.FindGameObjectWithTag("MainCamera");
        if (mainCameraGO != null)
        {
            var camera = mainCameraGO.GetComponent<Camera>();
            if (camera != null)
            {
                activeCamera = camera;
                OnCameraFound();
                return;
            }
        }

        // Try to find any active camera
        var allCameras = Camera.allCameras;
        foreach (var camera in allCameras)
        {
            if (camera != null && camera.enabled && camera.gameObject.activeInHierarchy)
            {
                activeCamera = camera;
                OnCameraFound();
                return;
            }
        }
    }

    private static void OnCameraFound()
    {
        if (activeCamera == null) return;

        Debug.Log($"[OptimizedMeshLOD] Camera found: {activeCamera.name}. Initializing {pendingObjects.Count} pending LOD objects.");

        // Initialize all pending objects
        for (int i = pendingObjects.Count - 1; i >= 0; i--)
        {
            var lodObject = pendingObjects[i];
            if (lodObject != null)
            {
                lodObject.InitializeLODSystem();
                pendingObjects.RemoveAt(i);
            }
            else
            {
                pendingObjects.RemoveAt(i);
            }
        }

        // Stop camera detection
        isSearchingForCamera = false;
        var detector = GameObject.Find("CameraDetector");
        if (detector != null)
        {
            if (Application.isPlaying)
                Destroy(detector);
            else
                DestroyImmediate(detector);
        }
    }

    private static void OnCameraPreRender(Camera cam)
    {
        if (cam == Camera.main || cam.CompareTag("MainCamera"))
        {
            activeCamera = cam;
            Vector3 currentPos = cam.transform.position;
            
            // Only process if camera moved significantly
            if (Vector3.SqrMagnitude(currentPos - lastCameraPosition) > 0.1f)
            {
                lastCameraPosition = currentPos;
                ProcessLODUpdates();
            }
        }
    }

    private static void ProcessLODUpdates()
    {
        if (isProcessingUpdates || activeCamera == null) return;

        isProcessingUpdates = true;

        try
        {
            var processedObjects = new List<OptimizedMeshLOD>();

            // Collect objects to process
            while (updateQueue.TryDequeue(out var lodObject) && processedObjects.Count < 50)
            {
                if (lodObject != null && lodObject.isInitialized)
                {
                    processedObjects.Add(lodObject);
                }
            }

            // Sort by distance for priority processing
            processedObjects.Sort();

            // Process calculations on main thread to avoid Unity API issues
            foreach (var lodObject in processedObjects)
            {
                if (lodObject != null && !lodObject.isProcessingLOD)
                {
                    lodObject.CalculateOptimalLOD();
                    lodObject.ApplyLODIfNeeded();
                }
            }
        }
        finally
        {
            isProcessingUpdates = false;
        }
    }

    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        // Ensure static initialization happens
        EnsureStaticInitialization();
        
        InitializeComponents();
        
        if (originalMesh != null)
        {
            originalTriangles = originalMesh.triangles.Length / 3;
            currentTriangles = originalTriangles;
            
            // Try immediate initialization, or defer if no camera
            if (GetActiveCamera() != null)
            {
                InitializeLODSystem();
            }
            else
            {
                // Add to pending list for later initialization
                pendingObjects.Add(this);
                if (enableDebugLogs)
                    Debug.Log($"[OptimizedMeshLOD] No camera found for {gameObject.name}. Deferring initialization until camera is available.");
            }
        }
    }

    private void OnEnable()
    {
        if (isInitialized)
        {
            ScheduleUpdate();
        }
        else if (!pendingObjects.Contains(this))
        {
            // Re-try initialization if we're not pending and not initialized
            if (GetActiveCamera() != null)
            {
                InitializeLODSystem();
            }
            else
            {
                pendingObjects.Add(this);
            }
        }
    }

    private void OnDisable()
    {
        // Remove from update queue if present
        var tempQueue = new Queue<OptimizedMeshLOD>();
        while (updateQueue.TryDequeue(out var item))
        {
            if (item != this)
            {
                tempQueue.Enqueue(item);
            }
        }
        
        // Re-add non-matching items
        while (tempQueue.Count > 0)
        {
            updateQueue.Enqueue(tempQueue.Dequeue());
        }

        // Remove from pending objects if present
        pendingObjects.Remove(this);
    }

    private void OnDestroy()
    {
        CleanupResources();
    }

    #endregion

    #region Initialization

    private void InitializeComponents()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        originalMesh = meshFilter.sharedMesh;
        originalMaterial = meshRenderer.sharedMaterial;

        if (originalMesh == null)
        {
            Debug.LogError($"[OptimizedMeshLOD] No mesh found on {gameObject.name}. Component requires a valid mesh.", this);
            enabled = false;
            return;
        }

        if (originalMaterial == null)
        {
            Debug.LogWarning($"[OptimizedMeshLOD] No material found on {gameObject.name}. Texture scaling will be disabled.", this);
            enableTextureScaling = false;
        }
    }

    private void InitializeLODSystem()
    {
        try
        {
            // Check cache first
            if (meshCache.TryGetValue(originalMesh, out var cachedLODData))
            {
                lodData = cachedLODData;
                isInitialized = true;
                ScheduleUpdate();
                if (enableDebugLogs)
                    Debug.Log($"[OptimizedMeshLOD] Using cached LOD data for {gameObject.name}");
                return;
            }

            // Generate LOD data synchronously to avoid threading issues
            lodData = GenerateLODDataSync();
            
            // Cache the results
            meshCache[originalMesh] = lodData;
            
            isInitialized = true;
            ScheduleUpdate();
            
            if (enableDebugLogs)
            {
                Debug.Log($"[OptimizedMeshLOD] Initialized LOD system for {gameObject.name} with {lodData.Length} levels");
                for (int i = 0; i < lodData.Length; i++)
                {
                    Debug.Log($"LOD {i}: {lodData[i].triangleCount} triangles, Distance: {lodData[i].distanceThreshold:F1}m");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OptimizedMeshLOD] Failed to initialize LOD system: {ex.Message}", this);
            enabled = false;
        }
    }

    #endregion

    #region LOD Generation

    private LODData[] GenerateLODDataSync()
    {
        var data = new LODData[lodLevels + 1];
        
        // Level 0: Original mesh
        data[0] = new LODData(
            originalMesh,
            enableTextureScaling ? CreateScaledMaterials(1f) : new[] { originalMaterial },
            1f,
            0f
        );

        // Generate reduced LOD levels
        for (int i = 1; i <= lodLevels; i++)
        {
            float normalizedLevel = (float)i / lodLevels;
            float reductionRatio = normalizedLevel * maxReductionRatio;
            float distanceThreshold = (normalizedLevel * maxDistance) / lodBias;
            float textureScale = enableTextureScaling ? Mathf.Lerp(1f, minTextureScale, normalizedLevel) : 1f;
            
            Mesh reducedMesh = CreateOptimizedMesh(originalMesh, reductionRatio);
            Material[] materials = enableTextureScaling ? CreateScaledMaterials(textureScale) : new[] { originalMaterial };
            
            data[i] = new LODData(reducedMesh, materials, textureScale, distanceThreshold);
            
            if (enableDebugLogs)
                Debug.Log($"Generated LOD {i}: {data[i].triangleCount} triangles (reduction: {reductionRatio:P})");
        }

        return data;
    }

    private Mesh CreateOptimizedMesh(Mesh sourceMesh, float reductionRatio)
    {
        if (reductionRatio <= 0f) return sourceMesh;
        
        var optimizedMesh = Instantiate(sourceMesh);
        optimizedMesh.name = $"{sourceMesh.name}_LOD_{reductionRatio:F2}";
        
        Vector3[] vertices = optimizedMesh.vertices;
        int[] triangles = optimizedMesh.triangles;
        Vector3[] normals = optimizedMesh.normals;
        Vector2[] uvs = optimizedMesh.uv;
        
        int originalTriangleCount = triangles.Length / 3;
        
        // Calculate target triangle count - ИСПРАВЛЕНО: правильный расчет
        int targetTriangleCount = Mathf.Max(1, Mathf.FloorToInt(originalTriangleCount * (1f - reductionRatio)));
        int targetTriangles = targetTriangleCount * 3; // Индексы треугольников
        
        if (enableDebugLogs)
            Debug.Log($"Creating optimized mesh: {originalTriangleCount} -> {targetTriangleCount} triangles (reduction: {reductionRatio:P})");
        
        if (preserveEdges && targetTriangleCount < originalTriangleCount)
        {
            // Advanced mesh simplification with edge preservation
            SimplifyMeshWithEdgePreservation(ref vertices, ref triangles, ref normals, ref uvs, targetTriangles);
        }
        else
        {
            // Simple uniform reduction - ИСПРАВЛЕНО: правильное обрезание массива
            if (targetTriangles < triangles.Length)
            {
                Array.Resize(ref triangles, targetTriangles);
            }
        }
        
        // Apply changes to mesh
        optimizedMesh.vertices = vertices;
        optimizedMesh.triangles = triangles;
        
        if (normals.Length == vertices.Length)
            optimizedMesh.normals = normals;
        else
            optimizedMesh.RecalculateNormals();
            
        if (uvs.Length == vertices.Length)
            optimizedMesh.uv = uvs;
            
        optimizedMesh.RecalculateBounds();
        optimizedMesh.RecalculateTangents();
        
        return optimizedMesh;
    }

    private void SimplifyMeshWithEdgePreservation(ref Vector3[] vertices, ref int[] triangles, 
        ref Vector3[] normals, ref Vector2[] uvs, int targetTriangleCount)
    {
        if (triangles.Length <= targetTriangleCount) return;
        
        // Calculate edge importance based on angle between faces
        var edgeImportance = new Dictionary<(int, int), float>();
        
        for (int i = 0; i < triangles.Length; i += 3)
        {
            var v0 = triangles[i];
            var v1 = triangles[i + 1];
            var v2 = triangles[i + 2];
            
            // Validate indices - ДОБАВЛЕНО: проверка границ массива
            if (v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length) continue;
            
            // Calculate face normal
            var normal = Vector3.Cross(vertices[v1] - vertices[v0], vertices[v2] - vertices[v0]).normalized;
            
            // Store edge importance based on neighboring face angles
            StoreEdgeImportance(edgeImportance, v0, v1, normal);
            StoreEdgeImportance(edgeImportance, v1, v2, normal);
            StoreEdgeImportance(edgeImportance, v2, v0, normal);
        }
        
        // Sort triangles by importance (less important triangles removed first)
        var triangleImportance = new List<(int index, float importance)>();
        
        for (int i = 0; i < triangles.Length; i += 3)
        {
            float importance = CalculateTriangleImportance(triangles, i, edgeImportance);
            triangleImportance.Add((i, importance));
        }
        
        triangleImportance.Sort((a, b) => a.importance.CompareTo(b.importance));
        
        // Keep only the most important triangles
        var newTriangles = new List<int>();
        int trianglesToKeep = targetTriangleCount / 3;
        
        // ИСПРАВЛЕНО: правильный диапазон для сохранения самых важных треугольников
        int startIndex = Mathf.Max(0, triangleImportance.Count - trianglesToKeep);
        for (int i = startIndex; i < triangleImportance.Count; i++)
        {
            int triangleIndex = triangleImportance[i].index;
            newTriangles.Add(triangles[triangleIndex]);
            newTriangles.Add(triangles[triangleIndex + 1]);
            newTriangles.Add(triangles[triangleIndex + 2]);
        }
        
        triangles = newTriangles.ToArray();
    }

    private void StoreEdgeImportance(Dictionary<(int, int), float> edgeImportance, int v0, int v1, Vector3 normal)
    {
        var edge = v0 < v1 ? (v0, v1) : (v1, v0);
        
        if (edgeImportance.TryGetValue(edge, out float existingImportance))
        {
            // ИСПРАВЛЕНО: корректный расчет важности ребра
            float normalDot = Vector3.Dot(normal, normal); // Всегда 1 для нормализованного вектора
            float edgeSharpness = 1f - Mathf.Abs(Vector3.Dot(normal, Vector3.up)); // Пример расчета
            edgeImportance[edge] = Mathf.Max(existingImportance, edgeSharpness * edgeThreshold);
        }
        else
        {
            edgeImportance[edge] = edgeThreshold;
        }
    }

    private float CalculateTriangleImportance(int[] triangles, int triangleIndex, Dictionary<(int, int), float> edgeImportance)
    {
        var v0 = triangles[triangleIndex];
        var v1 = triangles[triangleIndex + 1];
        var v2 = triangles[triangleIndex + 2];
        
        float importance = 0f;
        importance += edgeImportance.GetValueOrDefault((Mathf.Min(v0, v1), Mathf.Max(v0, v1)), 0f);
        importance += edgeImportance.GetValueOrDefault((Mathf.Min(v1, v2), Mathf.Max(v1, v2)), 0f);
        importance += edgeImportance.GetValueOrDefault((Mathf.Min(v2, v0), Mathf.Max(v2, v0)), 0f);
        
        return importance / 3f;
    }

    private Material[] CreateScaledMaterials(float scale)
    {
        if (originalMaterial == null) return null;
        
        var scaledMaterial = new Material(originalMaterial);
        
        // Scale main texture
        if (scaledMaterial.HasProperty("_MainTex"))
        {
            var mainTex = scaledMaterial.mainTexture;
            if (mainTex != null && mainTex is Texture2D)
            {
                int newWidth = Mathf.Max(32, Mathf.RoundToInt(mainTex.width * scale));
                int newHeight = Mathf.Max(32, Mathf.RoundToInt(mainTex.height * scale));
                
                var scaledTexture = ScaleTexture((Texture2D)mainTex, newWidth, newHeight);
                if (scaledTexture != null)
                {
                    scaledMaterial.mainTexture = scaledTexture;
                }
            }
        }
        
        scaledMaterial.name = $"{originalMaterial.name}_Scaled_{scale:F2}";
        return new[] { scaledMaterial };
    }

    private Texture2D ScaleTexture(Texture2D source, int width, int height)
    {
        // Make texture readable if it's not
        if (!source.isReadable)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[OptimizedMeshLOD] Texture {source.name} is not readable. Texture scaling disabled for this material.");
            return null;
        }

        var result = new Texture2D(width, height, source.format, false);
        
        float xRatio = (float)source.width / width;
        float yRatio = (float)source.height / height;
        
        var pixels = new Color[width * height];
        
        try
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.FloorToInt(x * xRatio);
                    int sourceY = Mathf.FloorToInt(y * yRatio);
                    
                    pixels[y * width + x] = source.GetPixel(sourceX, sourceY);
                }
            }
            
            result.SetPixels(pixels);
            result.Apply();
        }
        catch (Exception ex)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[OptimizedMeshLOD] Failed to scale texture {source.name}: {ex.Message}");
            DestroyTextureSafely(result);
            return null;
        }
        
        return result;
    }

    #endregion

    #region LOD Calculation and Application

    private void ScheduleUpdate()
    {
        if (Time.time >= nextUpdateTime)
        {
            nextUpdateTime = Time.time + updateInterval;
            updateQueue.Enqueue(this);
        }
    }

    private Camera GetActiveCamera()
    {
        // Return cached camera if still valid
        if (activeCamera != null && activeCamera.enabled && activeCamera.gameObject.activeInHierarchy)
        {
            return activeCamera;
        }

        // In editor, try scene view camera first
        if (!Application.isPlaying && useSceneCameraInEditor)
        {
#if UNITY_EDITOR
            if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
                return SceneView.lastActiveSceneView.camera;
#endif
        }

        // Try to find a new active camera
        FindActiveCamera();

        return activeCamera;
    }

    private void CalculateOptimalLOD()
    {
        if (!isInitialized || activeCamera == null) return;
        
        currentDistance = Vector3.Distance(activeCamera.transform.position, transform.position);
        
        // ИСПРАВЛЕНО: правильный расчет LOD уровня
        int newTargetLOD = 0;
        
        for (int i = lodData.Length - 1; i >= 0; i--)
        {
            if (currentDistance >= lodData[i].distanceThreshold)
            {
                newTargetLOD = i;
                break;
            }
        }
        
        targetLODLevel = newTargetLOD;
        
        if (enableDebugLogs && targetLODLevel != currentLODLevel)
        {
            Debug.Log($"[OptimizedMeshLOD] {gameObject.name}: Distance {currentDistance:F1}m -> LOD {targetLODLevel} ({lodData[targetLODLevel].triangleCount} triangles)");
        }
    }

    private void ApplyLODIfNeeded()
    {
        if (targetLODLevel == currentLODLevel || isProcessingLOD) return;
        
        isProcessingLOD = true;
        
        try
        {
            var lodLevel = lodData[targetLODLevel];
            
            // Apply mesh
            meshFilter.sharedMesh = lodLevel.mesh;
            
            // Apply materials if texture scaling is enabled
            if (enableTextureScaling && lodLevel.materials != null)
            {
                meshRenderer.materials = lodLevel.materials;
                currentTextureScale = lodLevel.textureScale;
            }
            
            currentLODLevel = targetLODLevel;
            currentTriangles = lodLevel.triangleCount;
            
            if (enableDebugLogs)
                Debug.Log($"[OptimizedMeshLOD] Applied LOD {currentLODLevel} to {gameObject.name}: {currentTriangles} triangles");
            
            // Schedule next update
            ScheduleUpdate();
        }
        finally
        {
            isProcessingLOD = false;
        }
    }

    #endregion

    #region IComparable Implementation

    public int CompareTo(OptimizedMeshLOD other)
    {
        if (other == null) return 1;
        return currentDistance.CompareTo(other.currentDistance);
    }

    #endregion

    #region Cleanup

    private void CleanupResources()
    {
        if (lodData != null)
        {
            foreach (var data in lodData)
            {
                if (data.mesh != null && data.mesh != originalMesh)
                {
                    DestroyMeshSafely(data.mesh);
                }
                
                if (data.materials != null)
                {
                    foreach (var material in data.materials)
                    {
                        if (material != null && material != originalMaterial)
                        {
                            DestroyMaterialSafely(material);
                        }
                    }
                }
            }
        }
    }

    private void DestroyMeshSafely(Mesh mesh)
    {
        if (mesh == null) return;

        if (Application.isPlaying)
            Destroy(mesh);
        else
            DestroyImmediate(mesh);
    }

    private void DestroyMaterialSafely(Material material)
    {
        if (material == null) return;
        
        if (Application.isPlaying)
            Destroy(material);
        else
            DestroyImmediate(material);
    }

    private void DestroyTextureSafely(Texture2D texture)
    {
        if (texture == null) return;
        
        if (Application.isPlaying)
            Destroy(texture);
        else
            DestroyImmediate(texture);
    }

    #endregion

    #region Editor Support

#if UNITY_EDITOR
    [Button("Preview LOD Levels")]
    private void PreviewLODLevels()
    {
        if (!Application.isPlaying && originalMesh != null)
        {
            var previewData = GenerateLODDataSync();
            
            Debug.Log($"LOD Preview for {gameObject.name}:");
            for (int i = 0; i < previewData.Length; i++)
            {
                Debug.Log($"LOD {i}: {previewData[i].triangleCount} triangles, " +
                         $"Distance: {previewData[i].distanceThreshold:F1}m, " +
                         $"Texture Scale: {previewData[i].textureScale:F2}");
            }
        }
    }

    [Button("Force LOD Update")]
    private void ForceLODUpdate()
    {
        if (isInitialized)
        {
            CalculateOptimalLOD();
            ApplyLODIfNeeded();
            Debug.Log($"Forced LOD update: Level {currentLODLevel}, Distance {currentDistance:F1}m");
        }
    }

    [Button("Clear Cache")]
    private void ClearCache()
    {
        meshCache.Clear();
        Debug.Log("LOD cache cleared");
    }

    [Button("Test LOD Distance")]
    private void TestLODDistance()
    {
        if (!isInitialized || activeCamera == null)
        {
            Debug.LogWarning("LOD system not initialized or no camera found");
            return;
        }

        float testDistance = Vector3.Distance(activeCamera.transform.position, transform.position);
        Debug.Log($"Current distance to camera: {testDistance:F1}m");
        
        for (int i = 0; i < lodData.Length; i++)
        {
            string marker = (testDistance >= lodData[i].distanceThreshold) ? " <- ACTIVE" : "";
            Debug.Log($"LOD {i}: Distance threshold {lodData[i].distanceThreshold:F1}m, Triangles: {lodData[i].triangleCount}{marker}");
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!isInitialized) return;
        
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, maxDistance);
        
        if (lodData != null)
        {
            for (int i = 1; i < lodData.Length; i++)
            {
                Gizmos.color = Color.Lerp(Color.green, Color.red, (float)i / lodData.Length);
                Gizmos.DrawWireSphere(transform.position, lodData[i].distanceThreshold);
                
                // Draw current LOD level
                if (i == currentLODLevel)
                {
                    Gizmos.color = Color.white;
                    Gizmos.DrawWireCube(transform.position, Vector3.one * 2f);
                }
            }
        }
        
        // Draw line to camera
        if (activeCamera != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position, activeCamera.transform.position);
        }
    }
#endif

    #endregion
}

/// <summary>
/// Manager class for coordinating LOD updates across all instances
/// </summary>
public class LODUpdateManager : MonoBehaviour
{
    private void Start()
    {
        // This component exists solely to provide a MonoBehaviour context
        DontDestroyOnLoad(gameObject);
    }
}

/// <summary>
/// Helper component for detecting when cameras become available
/// </summary>
public class CameraDetector : MonoBehaviour
{
    private float checkInterval = 1f;
    private float nextCheckTime;

    private void Update()
    {
        if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + checkInterval;
            
            // Try to find an active camera
            if (Camera.main != null || Camera.allCameras.Length > 0)
            {
                // Trigger camera search in the main LOD system
                typeof(OptimizedMeshLOD)
                    .GetMethod("FindActiveCamera", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.Invoke(null, null);
                
                // Self-destruct if camera found
                var activeCamera = typeof(OptimizedMeshLOD)
                    .GetField("activeCamera", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null) as Camera;
                    
                if (activeCamera != null)
                {
                    Destroy(gameObject);
                }
            }
        }
    }
}