using UnityEngine;

/// <summary>
/// Mirrors the user's jump directly onto the character's vertical position.
/// No animation clip needed — the character's transform Y follows the user's
/// hip Y displacement in real time.
///
/// ALGORITHM:
///   1. At Start: capture `_basePosition = transform.position`.
///   2. Each LateUpdate:
///      - Smooth the averaged hip Y (input smoothing).
///      - Compute hipDelta = BaselineHipY - smoothedHipY  (positive = user jumped UP).
///      - Deadzone + clamp (>= 0) so crouches don't push character down.
///      - Target offset = hipDelta * jumpHeightScale.
///      - Smooth offset toward target (output smoothing) — prevents snaps.
///      - Apply: transform.position = _basePosition + up * offset.
///
/// Y-AXIS NOTE: MediaPipe image Y is 0=top, 1=bottom. User jumping UP makes
/// hipY DECREASE. Therefore hipDelta = (baseline - current) is positive
/// when user is in the air.
///
/// REQUIRES:
///   - PoseDetectionManager reference assigned in Inspector.
///   - CalibrationManager.HasBaselineHipY == true (captured during calibration).
///
/// RUNS IN: LateUpdate — after Animator, so translation isn't overwritten.
/// </summary>
public class JumpDetector : MonoBehaviour
{
    [Header("Pose source")]
    public PoseDetectionManager poseManager;

    [Header("Mirror tuning")]
    [Range(0.5f, 10f)]
    [Tooltip("World units per unit of hip displacement. Higher = bigger visual jump.")]
    public float jumpHeightScale = 3.5f;

    [Range(0f, 0.1f)]
    [Tooltip("Ignore hip displacement below this value (suppresses micro-jitter).")]
    public float deadzone = 0.015f;

    [Range(1f, 30f)]
    [Tooltip("Smoothing on raw hip Y input — higher = more responsive.")]
    public float hipSmoothSpeed = 18f;

    [Range(1f, 30f)]
    [Tooltip("Smoothing on output Y offset — higher = snappier, lower = floatier.")]
    public float positionSmoothSpeed = 22f;

    [Range(0.1f, 0.9f)]
    [Tooltip("Minimum hip visibility. Both hips must pass; otherwise character eases back to ground.")]
    public float visibilityThreshold = 0.4f;

    private Vector3 _basePosition;
    private float _smoothedHipY;
    private float _targetYOffset;
    private float _currentYOffset;
    private bool _hasInitializedSmoothing;
    private bool _disabled;

    private void Start()
    {
        if (poseManager == null)
        {
            Debug.LogError("[JumpDetector] PoseManager not assigned. Disabling.");
            _disabled = true;
            return;
        }

        if (!CalibrationManager.HasBaselineHipY)
        {
            Debug.LogWarning("[JumpDetector] No baseline hip Y captured during calibration. Jump mirror disabled.");
            _disabled = true;
            return;
        }

        _basePosition = transform.position;
        Debug.Log($"[JumpDetector] Ready. basePos={_basePosition} baselineHipY={CalibrationManager.BaselineHipY:F3}");
    }

    private void LateUpdate()
    {
        if (_disabled) return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);

        // Visibility gate — if pose is lost, ease back to ground.
        if (poseManager.LeftHipVisibility  < visibilityThreshold ||
            poseManager.RightHipVisibility < visibilityThreshold)
        {
            _hasInitializedSmoothing = false;
            _targetYOffset = 0f;
            EaseAndApply(dt);
            return;
        }

        float rawHipY = 0.5f * (poseManager.LeftHip.y + poseManager.RightHip.y);

        // First valid frame — initialize smoothing; don't compute offset this frame.
        if (!_hasInitializedSmoothing)
        {
            _smoothedHipY = rawHipY;
            _hasInitializedSmoothing = true;
            _targetYOffset = 0f;
            EaseAndApply(dt);
            return;
        }

        // Input smoothing on hip Y
        float inBlend = 1f - Mathf.Exp(-hipSmoothSpeed * dt);
        _smoothedHipY = Mathf.Lerp(_smoothedHipY, rawHipY, inBlend);

        // Positive when user jumped UP (MediaPipe Y=0 is top)
        float hipDelta = CalibrationManager.BaselineHipY - _smoothedHipY;

        // Remove deadzone; clamp negative (crouch) to 0
        float adjusted = Mathf.Max(0f, hipDelta - deadzone);

        _targetYOffset = adjusted * jumpHeightScale;
        EaseAndApply(dt);
    }

    private void EaseAndApply(float dt)
    {
        float outBlend = 1f - Mathf.Exp(-positionSmoothSpeed * dt);
        _currentYOffset = Mathf.Lerp(_currentYOffset, _targetYOffset, outBlend);
        transform.position = _basePosition + Vector3.up * _currentYOffset;
    }
}
