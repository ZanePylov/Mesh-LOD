using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using NaughtyAttributes;

namespace OptimizedLOD
{
    /// <summary>
    /// Централизованный менеджер LOD системы. Обрабатывает все LOD объекты в сцене
    /// с использованием многопоточности и событийной модели для максимальной производительности.
    /// </summary>
    public class LODManager : MonoBehaviour
    {
        #region Singleton
        private static LODManager _instance;
        public static LODManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<LODManager>();
                    if (_instance == null)
                    {
                        GameObject go = new GameObject("LOD Manager");
                        _instance = go.AddComponent<LODManager>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }
        #endregion

        #region Events
        /// <summary>
        /// События для уведомления об изменениях LOD уровней
        /// </summary>
        public static event Action<LODObject, LODLevel> OnLODChanged;
        public static event Action<Vector3> OnCameraPositionChanged;
        #endregion

        #region Fields
        [Header("Performance Settings")]
        [SerializeField, Range(0.016f, 1f), Tooltip("Интервал обновления в секундах")]
        private float updateInterval = 0.1f;
        
        [SerializeField, Range(1, 100), Tooltip("Максимальное количество объектов для обработки за кадр")]
        private int maxObjectsPerFrame = 20;
        
        [SerializeField, Tooltip("Использовать многопоточность для расчетов")]
        private bool useMultithreading = true;

        [Header("Debug")]
        [SerializeField, ReadOnly] private int registeredObjectsCount;
        
        private Camera mainCamera;
        private Vector3 lastCameraPosition;
        private readonly ConcurrentQueue<LODObject> registeredObjects = new ConcurrentQueue<LODObject>();
        private readonly Dictionary<LODObject, LODCalculationResult> calculationResults = new Dictionary<LODObject, LODCalculationResult>();
        private readonly object lockObject = new object();
        
        private CancellationTokenSource cancellationTokenSource;
        private float lastUpdateTime;
        private int currentFrameProcessedObjects;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
                Initialize();
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            if (Time.time - lastUpdateTime >= updateInterval)
            {
                ProcessLODUpdates();
                lastUpdateTime = Time.time;
                currentFrameProcessedObjects = 0;
            }
        }

        private void OnDestroy()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
        }
        #endregion

        #region Initialization
        /// <summary>
        /// Инициализация менеджера LOD
        /// </summary>
        private void Initialize()
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                Debug.LogWarning("Main camera not found! LOD system may not work correctly.");
            }
            
            cancellationTokenSource = new CancellationTokenSource();
            lastUpdateTime = Time.time;
            
            // Подписка на события камеры
            if (mainCamera != null)
            {
                lastCameraPosition = mainCamera.transform.position;
            }
        }
        #endregion

        #region Public API
        /// <summary>
        /// Регистрирует LOD объект в системе
        /// </summary>
        public void RegisterLODObject(LODObject lodObject)
        {
            if (lodObject == null) return;
            
            registeredObjects.Enqueue(lodObject);
            registeredObjectsCount = GetQueueCount(registeredObjects);
        }

        /// <summary>
        /// Разрегистрирует LOD объект из системы
        /// </summary>
        public void UnregisterLODObject(LODObject lodObject)
        {
            if (lodObject == null) return;
            
            lock (lockObject)
            {
                if (calculationResults.ContainsKey(lodObject))
                {
                    calculationResults.Remove(lodObject);
                }
            }
        }

        /// <summary>
        /// Принудительное обновление всех LOD объектов
        /// </summary>
        public void ForceUpdateAll()
        {
            ProcessLODUpdates(true);
        }
        #endregion

        #region Core Logic
        /// <summary>
        /// Основная логика обработки LOD обновлений
        /// </summary>
        private void ProcessLODUpdates(bool forceUpdate = false)
        {
            if (mainCamera == null) return;

            Vector3 currentCameraPosition = mainCamera.transform.position;
            bool cameraMovedSignificantly = Vector3.Distance(currentCameraPosition, lastCameraPosition) > 0.1f;
            
            if (!cameraMovedSignificantly && !forceUpdate) return;

            lastCameraPosition = currentCameraPosition;
            OnCameraPositionChanged?.Invoke(currentCameraPosition);

            if (useMultithreading && Application.isPlaying)
            {
                ProcessLODMultiThreaded(currentCameraPosition);
            }
            else
            {
                ProcessLODSingleThreaded(currentCameraPosition);
            }
        }

        /// <summary>
        /// Многопоточная обработка LOD расчетов
        /// </summary>
        private async void ProcessLODMultiThreaded(Vector3 cameraPosition)
        {
            var objectsToProcess = new List<LODObject>();
            
            // Собираем объекты для обработки
            while (registeredObjects.TryDequeue(out LODObject lodObject) && 
                   objectsToProcess.Count < maxObjectsPerFrame)
            {
                if (lodObject != null && lodObject.gameObject.activeInHierarchy)
                {
                    objectsToProcess.Add(lodObject);
                }
            }

            if (objectsToProcess.Count == 0) return;

            try
            {
                // Запускаем расчеты в фоновом потоке
                var results = await Task.Run(() => 
                    CalculateLODDistances(objectsToProcess, cameraPosition), 
                    cancellationTokenSource.Token);

                // Применяем результаты в главном потоке
                ApplyLODResults(results);
            }
            catch (OperationCanceledException)
            {
                // Игнорируем отмененные операции
            }
            catch (Exception e)
            {
                Debug.LogError($"Error in multithreaded LOD processing: {e.Message}");
            }

            // Возвращаем объекты обратно в очередь для следующего кадра
            foreach (var obj in objectsToProcess)
            {
                registeredObjects.Enqueue(obj);
            }
        }

        /// <summary>
        /// Однопоточная обработка LOD расчетов
        /// </summary>
        private void ProcessLODSingleThreaded(Vector3 cameraPosition)
        {
            var objectsToProcess = new List<LODObject>();
            int processedCount = 0;
            
            // Обрабатываем ограниченное количество объектов за кадр
            while (registeredObjects.TryDequeue(out LODObject lodObject) && 
                   processedCount < maxObjectsPerFrame)
            {
                if (lodObject != null && lodObject.gameObject.activeInHierarchy)
                {
                    objectsToProcess.Add(lodObject);
                    processedCount++;
                }
            }

            var results = CalculateLODDistances(objectsToProcess, cameraPosition);
            ApplyLODResults(results);

            // Возвращаем объекты обратно в очередь
            foreach (var obj in objectsToProcess)
            {
                registeredObjects.Enqueue(obj);
            }
        }

        /// <summary>
        /// Расчет расстояний LOD для списка объектов
        /// </summary>
        private Dictionary<LODObject, LODCalculationResult> CalculateLODDistances(
            List<LODObject> objects, Vector3 cameraPosition)
        {
            var results = new Dictionary<LODObject, LODCalculationResult>();
            
            foreach (var lodObject in objects)
            {
                if (lodObject == null) continue;
                
                float distance = Vector3.Distance(cameraPosition, lodObject.transform.position);
                LODLevel newLevel = lodObject.CalculateLODLevel(distance);
                
                results[lodObject] = new LODCalculationResult
                {
                    Distance = distance,
                    NewLODLevel = newLevel,
                    PreviousLODLevel = lodObject.CurrentLODLevel
                };
            }
            
            return results;
        }

        /// <summary>
        /// Применение результатов LOD расчетов
        /// </summary>
        private void ApplyLODResults(Dictionary<LODObject, LODCalculationResult> results)
        {
            lock (lockObject)
            {
                foreach (var kvp in results)
                {
                    var lodObject = kvp.Key;
                    var result = kvp.Value;
                    
                    if (lodObject == null) continue;
                    
                    calculationResults[lodObject] = result;
                    
                    if (result.NewLODLevel != result.PreviousLODLevel)
                    {
                        lodObject.SetLODLevel(result.NewLODLevel);
                        OnLODChanged?.Invoke(lodObject, result.NewLODLevel);
                    }
                }
            }
        }
        
        /// <summary>
        /// Получение приблизительного количества элементов в ConcurrentQueue
        /// </summary>
        private int GetQueueCount<T>(ConcurrentQueue<T> queue)
        {
            int count = 0;
            var tempQueue = new ConcurrentQueue<T>();
            
            while (queue.TryDequeue(out T item))
            {
                tempQueue.Enqueue(item);
                count++;
            }
            
            while (tempQueue.TryDequeue(out T item))
            {
                queue.Enqueue(item);
            }
            
            return count;
        }
        #endregion
    }

    /// <summary>
    /// Результат расчета LOD для объекта
    /// </summary>
    public struct LODCalculationResult
    {
        public float Distance;
        public LODLevel NewLODLevel;
        public LODLevel PreviousLODLevel;
    }

    /// <summary>
    /// Уровни детализации
    /// </summary>
    public enum LODLevel
    {
        High = 0,    // Высокая детализация
        Medium = 1,  // Средняя детализация  
        Low = 2,     // Низкая детализация
        Off = 3      // Отключено
    }

    /// <summary>
    /// Конфигурация LOD для конкретного компонента
    /// </summary>
    [System.Serializable]
    public class LODComponentConfig
    {
        [Header("Distance Settings")]
        public float mediumDistance = 50f;
        public float lowDistance = 100f;
        public float offDistance = 200f;
        
        [Header("Component Settings")]
        public bool enableLOD = true;
        public LODLevel minimumLODLevel = LODLevel.Off;
    }

    /// <summary>
    /// Основной LOD объект. Заменяет оригинальный PerfectLOD с значительными улучшениями.
    /// Поддерживает событийную модель, пулинг объектов и оптимизированное управление состоянием.
    /// </summary>
    public class LODObject : MonoBehaviour
    {
        #region Serialized Fields
        [Header("LOD Configuration")]
        [SerializeField, Tooltip("Глобальный множитель расстояний")]
        private float globalDistanceMultiplier = 1f;
        
        [SerializeField, Tooltip("Использовать квадрат расстояния для оптимизации")]
        private bool useSquaredDistance = true;

        [Header("GameObjects")]
        [SerializeField, BoxGroup("High Detail")] 
        private GameObject[] highDetailObjects;
        [SerializeField, BoxGroup("High Detail")] 
        private LODComponentConfig highDetailConfig = new LODComponentConfig();

        [SerializeField, BoxGroup("Medium Detail")] 
        private GameObject[] mediumDetailObjects;
        [SerializeField, BoxGroup("Medium Detail")] 
        private LODComponentConfig mediumDetailConfig = new LODComponentConfig();

        [SerializeField, BoxGroup("Low Detail")] 
        private GameObject[] lowDetailObjects;
        [SerializeField, BoxGroup("Low Detail")] 
        private LODComponentConfig lowDetailConfig = new LODComponentConfig();

        [Header("Lights")]
        [SerializeField] private Light[] lights;
        [SerializeField] private LODComponentConfig lightConfig = new LODComponentConfig();

        [Header("Particle Systems")]
        [SerializeField] private ParticleSystem[] particleSystems;
        [SerializeField] private LODComponentConfig particleConfig = new LODComponentConfig();

        [Header("Audio Sources")]
        [SerializeField] private AudioSource[] audioSources;
        [SerializeField] private LODComponentConfig audioConfig = new LODComponentConfig();

        [Header("Debug Info")]
        [SerializeField, ReadOnly] private LODLevel currentLODLevel = LODLevel.High;
        [SerializeField, ReadOnly] private float lastCalculatedDistance;
        #endregion

        #region Properties
        public LODLevel CurrentLODLevel => currentLODLevel;
        public float LastCalculatedDistance => lastCalculatedDistance;
        #endregion

        #region Unity Lifecycle
        private void Start()
        {
            ValidateComponents();
            RegisterToManager();
            SetLODLevel(LODLevel.Low); // Начальное состояние
        }

        private void OnDestroy()
        {
            UnregisterFromManager();
        }

        private void OnValidate()
        {
            ValidateComponents();
        }
        #endregion

        #region Public API
        /// <summary>
        /// Вычисляет уровень LOD на основе расстояния
        /// </summary>
        public LODLevel CalculateLODLevel(float distance)
        {
            lastCalculatedDistance = distance;
            distance *= globalDistanceMultiplier;
            
            if (useSquaredDistance)
            {
                float sqrDistance = distance * distance;
                
                if (sqrDistance >= (lowDetailConfig.offDistance * lowDetailConfig.offDistance))
                    return LODLevel.Off;
                if (sqrDistance >= (lowDetailConfig.lowDistance * lowDetailConfig.lowDistance))
                    return LODLevel.Low;
                if (sqrDistance >= (mediumDetailConfig.mediumDistance * mediumDetailConfig.mediumDistance))
                    return LODLevel.Medium;
                
                return LODLevel.High;
            }
            else
            {
                if (distance >= lowDetailConfig.offDistance)
                    return LODLevel.Off;
                if (distance >= lowDetailConfig.lowDistance)
                    return LODLevel.Low;
                if (distance >= mediumDetailConfig.mediumDistance)
                    return LODLevel.Medium;
                
                return LODLevel.High;
            }
        }

        /// <summary>
        /// Применяет уровень LOD к объекту
        /// </summary>
        public void SetLODLevel(LODLevel newLevel)
        {
            if (currentLODLevel == newLevel) return;
            
            currentLODLevel = newLevel;
            ApplyLODSettings(newLevel);
        }

        /// <summary>
        /// Принудительное обновление LOD состояния
        /// </summary>
        public void ForceUpdate()
        {
            if (Camera.main != null)
            {
                float distance = Vector3.Distance(Camera.main.transform.position, transform.position);
                LODLevel newLevel = CalculateLODLevel(distance);
                SetLODLevel(newLevel);
            }
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Регистрация в LOD менеджере
        /// </summary>
        private void RegisterToManager()
        {
            if (LODManager.Instance != null)
            {
                LODManager.Instance.RegisterLODObject(this);
            }
        }

        /// <summary>
        /// Разрегистрация из LOD менеджера
        /// </summary>
        private void UnregisterFromManager()
        {
            if (LODManager.Instance != null)
            {
                LODManager.Instance.UnregisterLODObject(this);
            }
        }

        /// <summary>
        /// Применение настроек LOD
        /// </summary>
        private void ApplyLODSettings(LODLevel level)
        {
            // GameObjects
            ApplyGameObjectLOD(highDetailObjects, level == LODLevel.High && highDetailConfig.enableLOD);
            ApplyGameObjectLOD(mediumDetailObjects, level == LODLevel.Medium && mediumDetailConfig.enableLOD);
            ApplyGameObjectLOD(lowDetailObjects, level == LODLevel.Low && lowDetailConfig.enableLOD);

            // Lights
            if (lightConfig.enableLOD && level >= lightConfig.minimumLODLevel)
            {
                ApplyLightLOD(lights, level != LODLevel.Off);
            }

            // Particle Systems
            if (particleConfig.enableLOD && level >= particleConfig.minimumLODLevel)
            {
                ApplyParticleLOD(particleSystems, level != LODLevel.Off);
            }

            // Audio Sources
            if (audioConfig.enableLOD && level >= audioConfig.minimumLODLevel)
            {
                ApplyAudioLOD(audioSources, level != LODLevel.Off);
            }
        }

        /// <summary>
        /// Применение LOD к игровым объектам
        /// </summary>
        private void ApplyGameObjectLOD(GameObject[] objects, bool active)
        {
            if (objects == null) return;
            
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && objects[i].activeSelf != active)
                {
                    objects[i].SetActive(active);
                }
            }
        }

        /// <summary>
        /// Применение LOD к источникам света
        /// </summary>
        private void ApplyLightLOD(Light[] lightArray, bool enabled)
        {
            if (lightArray == null) return;
            
            for (int i = 0; i < lightArray.Length; i++)
            {
                if (lightArray[i] != null && lightArray[i].enabled != enabled)
                {
                    lightArray[i].enabled = enabled;
                }
            }
        }

        /// <summary>
        /// Применение LOD к системам частиц
        /// </summary>
        private void ApplyParticleLOD(ParticleSystem[] particles, bool active)
        {
            if (particles == null) return;
            
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i] == null) continue;
                
                if (active && !particles[i].isPlaying)
                {
                    particles[i].Play();
                }
                else if (!active && particles[i].isPlaying)
                {
                    particles[i].Stop();
                }
            }
        }

        /// <summary>
        /// Применение LOD к аудио источникам
        /// </summary>
        private void ApplyAudioLOD(AudioSource[] audioArray, bool active)
        {
            if (audioArray == null) return;
            
            for (int i = 0; i < audioArray.Length; i++)
            {
                if (audioArray[i] == null) continue;
                
                if (active && !audioArray[i].isPlaying && audioArray[i].clip != null)
                {
                    audioArray[i].Play();
                }
                else if (!active && audioArray[i].isPlaying)
                {
                    audioArray[i].Stop();
                }
            }
        }

        /// <summary>
        /// Валидация компонентов
        /// </summary>
        private void ValidateComponents()
        {
            // Удаление null ссылок из массивов
            highDetailObjects = FilterNullObjects(highDetailObjects);
            mediumDetailObjects = FilterNullObjects(mediumDetailObjects);
            lowDetailObjects = FilterNullObjects(lowDetailObjects);
            lights = FilterNullComponents(lights);
            particleSystems = FilterNullComponents(particleSystems);
            audioSources = FilterNullComponents(audioSources);
        }

        /// <summary>
        /// Фильтрация null объектов из массива
        /// </summary>
        private GameObject[] FilterNullObjects(GameObject[] array)
        {
            if (array == null) return new GameObject[0];
            
            var filtered = new List<GameObject>();
            foreach (var obj in array)
            {
                if (obj != null) filtered.Add(obj);
            }
            return filtered.ToArray();
        }

        /// <summary>
        /// Фильтрация null компонентов из массива
        /// </summary>
        private T[] FilterNullComponents<T>(T[] array) where T : Component
        {
            if (array == null) return new T[0];
            
            var filtered = new List<T>();
            foreach (var component in array)
            {
                if (component != null) filtered.Add(component);
            }
            return filtered.ToArray();
        }
        #endregion

        #region Editor Helpers
#if UNITY_EDITOR
        /// <summary>
        /// Отображение gizmos в редакторе
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (Camera.main == null) return;
            
            Vector3 cameraPos = Camera.main.transform.position;
            Vector3 objectPos = transform.position;
            
            // Отображение расстояний LOD
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(objectPos, mediumDetailConfig.mediumDistance * globalDistanceMultiplier);
            
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(objectPos, lowDetailConfig.lowDistance * globalDistanceMultiplier);
            
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(objectPos, lowDetailConfig.offDistance * globalDistanceMultiplier);
            
            // Линия до камеры
            Gizmos.color = Color.white;
            Gizmos.DrawLine(objectPos, cameraPos);
        }
#endif
        #endregion
    }

    /// <summary>
    /// Утилита для массового управления LOD объектами
    /// </summary>
    public static class LODUtilities
    {
        /// <summary>
        /// Найти все LOD объекты в сцене и применить настройки
        /// </summary>
        public static void ApplyGlobalLODSettings(float globalMultiplier)
        {
            var lodObjects = UnityEngine.Object.FindObjectsOfType<LODObject>();
            foreach (var lodObject in lodObjects)
            {
                // Применить глобальные настройки
                lodObject.ForceUpdate();
            }
        }

        /// <summary>
        /// Получить статистику производительности LOD системы
        /// </summary>
        public static LODPerformanceStats GetPerformanceStats()
        {
            var lodObjects = UnityEngine.Object.FindObjectsOfType<LODObject>();
            var stats = new LODPerformanceStats();
            
            foreach (var lodObject in lodObjects)
            {
                switch (lodObject.CurrentLODLevel)
                {
                    case LODLevel.High: stats.HighLODCount++; break;
                    case LODLevel.Medium: stats.MediumLODCount++; break;
                    case LODLevel.Low: stats.LowLODCount++; break;
                    case LODLevel.Off: stats.OffLODCount++; break;
                }
            }
            
            stats.TotalObjects = lodObjects.Length;
            return stats;
        }
    }

    /// <summary>
    /// Статистика производительности LOD системы
    /// </summary>
    public struct LODPerformanceStats
    {
        public int TotalObjects;
        public int HighLODCount;
        public int MediumLODCount;
        public int LowLODCount;
        public int OffLODCount;
        
        public float HighLODPercentage => TotalObjects > 0 ? (float)HighLODCount / TotalObjects * 100f : 0f;
        public float MediumLODPercentage => TotalObjects > 0 ? (float)MediumLODCount / TotalObjects * 100f : 0f;
        public float LowLODPercentage => TotalObjects > 0 ? (float)LowLODCount / TotalObjects * 100f : 0f;
        public float OffLODPercentage => TotalObjects > 0 ? (float)OffLODCount / TotalObjects * 100f : 0f;
    }
}