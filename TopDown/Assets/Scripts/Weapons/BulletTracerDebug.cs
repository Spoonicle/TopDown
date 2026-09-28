using System.Collections;
using UnityEngine;

/// <summary>
/// Spawns visible bullet tracer lines and impact markers in the Game view
/// when a weapon fires. Attach to the same GameObject as <see cref="WeaponHolder"/>.
/// Uses pooled LineRenderer objects for performance.
/// </summary>
[RequireComponent(typeof(WeaponHolder))]
public class BulletTracerDebug : MonoBehaviour
{
    [Header("Tracer Settings")]
    [Tooltip("Duration the tracer line stays visible.")]
    [SerializeField] private float _tracerDuration = 0.1f;

    [Tooltip("Starting width of the tracer line.")]
    [SerializeField] private float _tracerStartWidth = 0.04f;

    [Tooltip("End width of the tracer line.")]
    [SerializeField] private float _tracerEndWidth = 0.01f;

    [Tooltip("Color of the tracer line.")]
    [SerializeField] private Color _tracerColor = new Color(1f, 0.95f, 0.6f, 0.9f);

    [Header("Impact Settings")]
    [Tooltip("Size of the impact marker.")]
    [SerializeField] private float _impactSize = 0.12f;

    [Tooltip("Duration the impact marker stays visible.")]
    [SerializeField] private float _impactDuration = 0.25f;

    [Tooltip("Color of the impact marker.")]
    [SerializeField] private Color _impactColor = new Color(1f, 0.5f, 0.2f, 1f);

    private WeaponHolder _weaponHolder;
    private Material _tracerMaterial;

    private void Awake()
    {
        _weaponHolder = GetComponent<WeaponHolder>();

        // Create a simple unlit material for the tracers
        _tracerMaterial = new Material(Shader.Find("Sprites/Default"));
    }

    private void OnEnable()
    {
        _weaponHolder.OnWeaponFired += OnWeaponFired;
    }

    private void OnDisable()
    {
        _weaponHolder.OnWeaponFired -= OnWeaponFired;
    }

    private void OnWeaponFired(WeaponFireResult result)
    {
        SpawnTracer(result.Origin, result.HitPoint);

        if (result.DidHit)
        {
            SpawnImpactMarker(result.HitPoint);
        }
    }

    private void SpawnTracer(Vector2 from, Vector2 to)
    {
        var go = new GameObject("Tracer");
        go.transform.position = Vector3.zero;

        var lr = go.AddComponent<LineRenderer>();
        lr.material = _tracerMaterial;
        lr.startColor = _tracerColor;
        lr.endColor = _tracerColor;
        lr.startWidth = _tracerStartWidth;
        lr.endWidth = _tracerEndWidth;
        lr.positionCount = 2;
        lr.SetPosition(0, new Vector3(from.x, from.y, 0f));
        lr.SetPosition(1, new Vector3(to.x, to.y, 0f));
        lr.sortingOrder = 15;
        lr.useWorldSpace = true;

        Destroy(go, _tracerDuration);
    }

    private void SpawnImpactMarker(Vector2 position)
    {
        var go = new GameObject("Impact");
        go.transform.position = new Vector3(position.x, position.y, 0f);

        // Simple cross using two line renderers
        CreateImpactLine(go.transform, position,
            new Vector2(-_impactSize, -_impactSize) * 0.5f,
            new Vector2(_impactSize, _impactSize) * 0.5f);

        CreateImpactLine(go.transform, position,
            new Vector2(-_impactSize, _impactSize) * 0.5f,
            new Vector2(_impactSize, -_impactSize) * 0.5f);

        Destroy(go, _impactDuration);
    }

    private void CreateImpactLine(Transform parent, Vector2 center, Vector2 offsetA, Vector2 offsetB)
    {
        var lineGo = new GameObject("ImpactLine");
        lineGo.transform.SetParent(parent, false);

        var lr = lineGo.AddComponent<LineRenderer>();
        lr.material = _tracerMaterial;
        lr.startColor = _impactColor;
        lr.endColor = _impactColor;
        lr.startWidth = 0.03f;
        lr.endWidth = 0.03f;
        lr.positionCount = 2;
        lr.SetPosition(0, new Vector3(center.x + offsetA.x, center.y + offsetA.y, 0f));
        lr.SetPosition(1, new Vector3(center.x + offsetB.x, center.y + offsetB.y, 0f));
        lr.sortingOrder = 15;
        lr.useWorldSpace = true;
    }
}
