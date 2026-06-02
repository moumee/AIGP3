using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class PrefabPlacer : MonoBehaviour
{
    public enum BoundsSource
    {
        Renderers,
        Colliders,
        RenderersAndColliders
    }

    [Header("프리팹")]
    public GameObject prefab;

    [Header("Grid 설정")]
    [Min(1)] public int countX = 3;
    [Min(1)] public int countZ = 3;

    [Header("자동 간격")]
    public bool autoSpacing = true;

    [Tooltip("Renderer/Collider 중 어떤 기준으로 프리팹 크기를 계산할지")]
    public BoundsSource boundsSource = BoundsSource.RenderersAndColliders;

    [Min(0f)] public float paddingX = 5f;
    [Min(0f)] public float paddingZ = 5f;

    [Header("수동 간격 / 자동 계산 실패 시 fallback")]
    [Min(0)] public float spacingX = 20f;
    [Min(0)] public float spacingZ = 20f;

    [Header("복제 여부")]
    [Tooltip("OFF: 복제 없이 원본 하나만 사용 (단일 에이전트 테스트)")]
    public bool enableCloning = true;

    private readonly List<GameObject> _spawned = new();

    private void Awake()
    {
        if (!enableCloning) return;

        if (prefab == null)
        {
            Debug.LogWarning("[PrefabPlacer] prefab이 비어 있습니다.");
            return;
        }

        SpawnAll();
    }

    public void SpawnAll()
    {
        ClearSpawned();

        Vector2 effectiveSpacing = GetEffectiveSpacing();

        float useSpacingX = effectiveSpacing.x;
        float useSpacingZ = effectiveSpacing.y;

        float totalX = (countX - 1) * useSpacingX;
        float totalZ = (countZ - 1) * useSpacingZ;
        Vector3 origin = transform.position;

        int idx = 0;

        for (int z = 0; z < countZ; z++)
        {
            for (int x = 0; x < countX; x++)
            {
                Vector3 pos = origin + new Vector3(
                    x * useSpacingX - totalX * 0.5f,
                    0f,
                    z * useSpacingZ - totalZ * 0.5f);

                GameObject go = Instantiate(prefab, pos, Quaternion.identity);
                go.name = $"{prefab.name}_{idx++:D2}";
                _spawned.Add(go);
            }
        }

        Debug.Log($"[PrefabPlacer] {_spawned.Count}개 배치 완료 / spacing=({useSpacingX:F2}, {useSpacingZ:F2})");
    }

    public void ClearSpawned()
    {
        foreach (var go in _spawned)
        {
            if (go != null)
                Destroy(go);
        }

        _spawned.Clear();
    }

    private Vector2 GetEffectiveSpacing()
    {
        if (!autoSpacing)
            return new Vector2(spacingX, spacingZ);

        if (prefab == null)
            return new Vector2(spacingX, spacingZ);

        if (TryCalculatePrefabBounds(out Bounds bounds))
        {
            float autoX = bounds.size.x + paddingX;
            float autoZ = bounds.size.z + paddingZ;

            if (autoX > 0.001f && autoZ > 0.001f)
                return new Vector2(autoX, autoZ);
        }

        Debug.LogWarning("[PrefabPlacer] 프리팹 Bounds 계산 실패. 수동 spacing 값을 사용합니다.");
        return new Vector2(spacingX, spacingZ);
    }

    private bool TryCalculatePrefabBounds(out Bounds result)
    {
        result = default;
        bool hasBounds = false;

        bool useRenderers =
            boundsSource == BoundsSource.Renderers ||
            boundsSource == BoundsSource.RenderersAndColliders;

        bool useColliders =
            boundsSource == BoundsSource.Colliders ||
            boundsSource == BoundsSource.RenderersAndColliders;

        if (useRenderers)
        {
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;

                Bounds worldLikeBounds = TransformBounds(r.localBounds, r.transform.localToWorldMatrix);
                Encapsulate(ref result, worldLikeBounds, ref hasBounds);
            }
        }

        if (useColliders)
        {
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);

            foreach (Collider c in colliders)
            {
                if (c == null) continue;

                if (TryGetColliderLocalBounds(c, out Bounds localBounds))
                {
                    Bounds worldLikeBounds = TransformBounds(localBounds, c.transform.localToWorldMatrix);
                    Encapsulate(ref result, worldLikeBounds, ref hasBounds);
                }
            }
        }

        return hasBounds;
    }

    private static void Encapsulate(ref Bounds totalBounds, Bounds newBounds, ref bool hasBounds)
    {
        if (!hasBounds)
        {
            totalBounds = newBounds;
            hasBounds = true;
        }
        else
        {
            totalBounds.Encapsulate(newBounds);
        }
    }

    private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
    {
        Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;

        Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));

        Vector3 worldExtents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z)
        );

        return new Bounds(center, worldExtents * 2f);
    }

    private static bool TryGetColliderLocalBounds(Collider col, out Bounds bounds)
    {
        bounds = default;

        switch (col)
        {
            case BoxCollider box:
                bounds = new Bounds(box.center, box.size);
                return true;

            case SphereCollider sphere:
                float diameter = sphere.radius * 2f;
                bounds = new Bounds(sphere.center, Vector3.one * diameter);
                return true;

            case CapsuleCollider capsule:
                float capsuleDiameter = capsule.radius * 2f;
                Vector3 size = new Vector3(capsuleDiameter, capsuleDiameter, capsuleDiameter);
                size[capsule.direction] = Mathf.Max(capsule.height, capsuleDiameter);

                bounds = new Bounds(capsule.center, size);
                return true;

            case MeshCollider mesh when mesh.sharedMesh != null:
                bounds = mesh.sharedMesh.bounds;
                return true;
        }

        return false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector2 effectiveSpacing = Application.isPlaying
            ? GetEffectiveSpacing()
            : GetEffectiveSpacing();

        float useSpacingX = effectiveSpacing.x;
        float useSpacingZ = effectiveSpacing.y;

        float totalX = (countX - 1) * useSpacingX;
        float totalZ = (countZ - 1) * useSpacingZ;
        Vector3 origin = transform.position;

        Gizmos.color = enableCloning
            ? new Color(0.2f, 0.9f, 0.4f, 0.8f)
            : new Color(0.6f, 0.6f, 0.6f, 0.5f);

        Vector3 cubeSize = new Vector3(
            Mathf.Max(1f, useSpacingX * 0.9f),
            0.1f,
            Mathf.Max(1f, useSpacingZ * 0.9f)
        );

        for (int z = 0; z < countZ; z++)
        {
            for (int x = 0; x < countX; x++)
            {
                Vector3 pos = origin + new Vector3(
                    x * useSpacingX - totalX * 0.5f,
                    0f,
                    z * useSpacingZ - totalZ * 0.5f);

                Gizmos.DrawWireCube(pos, cubeSize);
            }
        }
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(PrefabPlacer))]
public class PrefabPlacerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var placer = (PrefabPlacer)target;

        EditorGUILayout.Space(4);

        Vector2 spacing = new Vector2(placer.spacingX, placer.spacingZ);

        if (placer.prefab != null && placer.autoSpacing)
        {
            // private 메서드라 여기서는 직접 계산하지 않고 안내만 표시
            EditorGUILayout.HelpBox(
                "자동 간격 ON: 프리팹의 Renderer/Collider Bounds + Padding으로 간격을 계산합니다.",
                MessageType.Info);
        }

        EditorGUILayout.HelpBox(
            placer.enableCloning
                ? $"Play 시 {placer.countX * placer.countZ}개 배치"
                : "복제 비활성 — 원본 오브젝트로 단일 테스트",
            placer.enableCloning ? MessageType.Info : MessageType.Warning);

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶ 지금 배치")) placer.SpawnAll();
                if (GUILayout.Button("🗑 전체 제거")) placer.ClearSpawned();
            }
        }
    }
}
#endif