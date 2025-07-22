using UnityEngine;
using UnityEditor;
using NaughtyAttributes;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
public class MeshLOD : MonoBehaviour
{
    [BoxGroup("LOD Settings"), SerializeField, Range(1, 100)]
    private int maxReductionPercent = 90; // Максимальное сжатие (процент удаления треугольников)

    [BoxGroup("LOD Settings"), SerializeField]
    private int lodLevels = 3; // Количество уровней детализации (LOD)

    [BoxGroup("LOD Settings"), SerializeField]
    private float maxDistance = 50f; // Максимальная дистанция, при которой применяется LOD

    [BoxGroup("LOD Settings"), SerializeField]
    private bool useSceneCameraInEditor = true; // Использовать камеру сцены в режиме редактора

    [BoxGroup("Debug Info"), ReadOnly, SerializeField]
    private int currentTriangles; // Текущее количество треугольников

    [BoxGroup("Debug Info"), ReadOnly, SerializeField]
    private int originalTriangles; // Исходное количество треугольников

    private MeshFilter meshFilter;
    private Mesh originalMesh;
    private Mesh[] lodMeshes; // Массив для хранения различных уровней LOD
    private Camera activeCamera;
    private int currentLOD = 0; // Текущий уровень детализации (LOD)

    private void Awake()
    {
        // Инициализация MeshFilter и проверка меша
        meshFilter = GetComponent<MeshFilter>();
        originalMesh = meshFilter.sharedMesh;

        if (originalMesh == null)
        {
            Debug.LogError($"No Mesh found on {gameObject.name}. MeshLOD requires a valid MeshFilter with a Mesh.");
            return;
        }

        // Сохраняем информацию об оригинальном меше
        originalTriangles = originalMesh.triangles.Length / 3;
        currentTriangles = originalTriangles;

        // Генерируем несколько уровней LOD
        GenerateLODMeshes();
    }

    private void GenerateLODMeshes()
    {
        lodMeshes = new Mesh[lodLevels + 1]; // +1 для полного уровня (100%)

        // Генерация LOD
        lodMeshes[0] = originalMesh; // Полный LOD (100%)
        for (int i = 1; i <= lodLevels; i++)
        {
            float reductionPercent = (i / (float)lodLevels) * maxReductionPercent;
            lodMeshes[i] = CreateReducedMesh(reductionPercent);
        }
    }

    private Mesh CreateReducedMesh(float reductionPercent)
    {
        // Уменьшаем количество полигонов в исходном меше на указанный процент
        Mesh reducedMesh = Instantiate(originalMesh);
        Vector3[] vertices = reducedMesh.vertices;
        int[] triangles = reducedMesh.triangles;

        // Вычисляем целевое количество треугольников
        int targetTriangleCount = Mathf.FloorToInt(triangles.Length * (1 - (reductionPercent / 100f)));
        targetTriangleCount = Mathf.Max(targetTriangleCount, 3); // Минимум 1 треугольник

        // Создаём новый список треугольников для упрощённого меша
        List<int> newTriangles = new List<int>();
        for (int i = 0; i < targetTriangleCount; i += 3)
        {
            newTriangles.Add(triangles[i]);
            if (i + 1 < triangles.Length) newTriangles.Add(triangles[i + 1]);
            if (i + 2 < triangles.Length) newTriangles.Add(triangles[i + 2]);
        }

        // Применяем изменения к мешу
        reducedMesh.triangles = newTriangles.ToArray();
        reducedMesh.RecalculateNormals();

        return reducedMesh;
    }

    private void Update()
    {
        // Получаем активную камеру
        activeCamera = GetActiveCamera();
        if (activeCamera == null) return;

        // Проверяем расстояние до камеры
        float distance = Vector3.Distance(activeCamera.transform.position, transform.position);

        // Рассчитываем уровень LOD на основе расстояния
        int targetLOD = CalculateLOD(distance);

        // Если целевой LOD отличается от текущего, применяем его
        if (targetLOD != currentLOD)
        {
            ApplyLOD(targetLOD);
            currentLOD = targetLOD;
        }
    }

    private Camera GetActiveCamera()
    {
        // В редакторе используем камеру сцены, если включено
        if (!Application.isPlaying && useSceneCameraInEditor)
        {
            if (SceneView.lastActiveSceneView != null)
                return SceneView.lastActiveSceneView.camera;
        }

        // В игре используем основную камеру
        return Camera.main;
    }

    private int CalculateLOD(float distance)
    {
        if (distance > maxDistance) return lodLevels; // Самое сильное сжатие (максимальный LOD)

        float normalizedDistance = distance / maxDistance;
        return Mathf.FloorToInt(normalizedDistance * lodLevels); // Рассчитываем LOD на основе нормализованного расстояния
    }

    private void ApplyLOD(int lodLevel)
    {
        // Применяем соответствующий меш из массива LOD
        meshFilter.sharedMesh = lodMeshes[lodLevel];
        currentTriangles = lodMeshes[lodLevel].triangles.Length / 3;
    }
}
