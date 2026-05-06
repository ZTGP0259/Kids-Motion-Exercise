using UnityEngine;
using UnityEngine.UI;

public class PunchCircleGameManager : MonoBehaviour
{
    private enum TargetSide
    {
        Left,
        Right
    }

    [Header("Pose Source")]
    public PoseDetectionManager poseManager;

    [Header("Character Hit Source")]
    public Animator characterAnimator;
    public Camera worldCamera;
    public bool useCharacterHandHit = true;
    public bool useForearmCapsuleHit = false;
    public bool showDebugHandPoint = true;
    [Range(0.01f, 0.15f)]
    public float handPointRadiusNormalized = 0.055f;

    [Header("Round")]
    [Range(30f, 60f)]
    public float roundDurationSeconds = 60f;
    public bool startWhenPoseReady = true;

    [Header("Target")]
    [Range(80f, 240f)]
    public float targetSizePixels = 170f;
    [Range(0.05f, 0.25f)]
    public float targetHitRadiusNormalized = 0.12f;
    [Range(0.05f, 0.45f)]
    public float targetHorizontalInset = 0.34f;
    [Range(0.25f, 0.75f)]
    public float targetVerticalPosition = 0.48f;

    [Header("Hit Rules")]
    public bool mirrorInputX = true;
    [Range(0.1f, 0.9f)]
    public float requiredVisibility = 0.35f;
    [Range(0.1f, 1f)]
    public float respawnDelaySeconds = 0.35f;
    [Range(0f, 0.3f)]
    public float spawnOverlapCheckDelay = 0.1f;

    private Canvas _canvas;
    private Text _scoreText;
    private Text _timerText;
    private Text _statusText;
    private Image _targetImage;
    private Image _debugHandImage;

    private TargetSide _targetSide;
    private Vector2 _targetNormalizedPosition;
    private bool _isRunning;
    private bool _isRoundOver;
    private bool _targetActive;
    private bool _needsReleaseBeforeHit;
    private bool _checkedSpawnOverlap;
    private float _timeRemaining;
    private float _respawnTimer;
    private float _targetSpawnTime;
    private int _score;

    private Transform _leftHandBone;
    private Transform _rightHandBone;
    private Transform _leftLowerArmBone;
    private Transform _rightLowerArmBone;

    private void Awake()
    {
        if (poseManager == null)
            poseManager = GetComponent<PoseDetectionManager>();

        ResolveCharacterReferences();
        BuildHud();
        _timeRemaining = roundDurationSeconds;
        UpdateHud("Get ready");
        HideTarget();
    }

    private void Update()
    {
        if (poseManager == null)
        {
            UpdateHud("Pose manager missing");
            return;
        }

        if (!_isRunning)
        {
            if (startWhenPoseReady && poseManager.IsReady)
                StartRound();
            else
                UpdateHud("Waiting for camera");
            return;
        }

        if (_isRoundOver)
            return;

        _timeRemaining -= Time.deltaTime;
        if (_timeRemaining <= 0f)
        {
            EndRound();
            return;
        }

        UpdateHud(null);
        UpdateDebugHandPoint();

        if (!_targetActive)
        {
            _respawnTimer -= Time.deltaTime;
            if (_respawnTimer <= 0f)
                SpawnTarget();
            return;
        }

        bool isTouching = IsCorrectHandTouchingTarget();
        if (!_checkedSpawnOverlap && Time.time - _targetSpawnTime >= spawnOverlapCheckDelay)
        {
            _needsReleaseBeforeHit = isTouching;
            _checkedSpawnOverlap = true;
        }

        if (!_checkedSpawnOverlap)
            return;

        if (_needsReleaseBeforeHit)
        {
            if (!isTouching)
                _needsReleaseBeforeHit = false;
            return;
        }

        if (isTouching)
            ScoreHit();
    }

    private void StartRound()
    {
        _score = 0;
        _timeRemaining = roundDurationSeconds;
        _isRunning = true;
        _isRoundOver = false;
        _targetActive = false;
        _respawnTimer = 0f;
        SpawnTarget();
        UpdateHud(null);
    }

    private void EndRound()
    {
        _timeRemaining = 0f;
        _isRoundOver = true;
        _targetActive = false;
        HideTarget();
        UpdateHud($"Finished  Score: {_score}");
    }

    private void ScoreHit()
    {
        _score++;
        _targetActive = false;
        _respawnTimer = respawnDelaySeconds;
        HideTarget();
        UpdateHud(null);
    }

    private void SpawnTarget()
    {
        _targetSide = Random.value < 0.5f ? TargetSide.Left : TargetSide.Right;

        float x = _targetSide == TargetSide.Left ? targetHorizontalInset : 1f - targetHorizontalInset;
        _targetNormalizedPosition = new Vector2(x, targetVerticalPosition);
        _targetActive = true;
        _needsReleaseBeforeHit = false;
        _checkedSpawnOverlap = false;
        _targetSpawnTime = Time.time;

        if (_targetImage == null)
            return;

        RectTransform rt = _targetImage.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(x, 1f - targetVerticalPosition);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(targetSizePixels, targetSizePixels);
        _targetImage.enabled = true;
    }

    private void HideTarget()
    {
        if (_targetImage != null)
            _targetImage.enabled = false;
        if (_debugHandImage != null)
            _debugHandImage.enabled = false;
    }

    private bool IsCorrectHandTouchingTarget()
    {
        ResolveCharacterReferences();

        Transform activeHand = _targetSide == TargetSide.Left ? _rightHandBone : _leftHandBone;
        Transform activeLowerArm = _targetSide == TargetSide.Left ? _rightLowerArmBone : _leftLowerArmBone;

        if (useCharacterHandHit && activeHand != null && TryWorldToPosePoint(activeHand.position, out Vector2 handPoint))
        {
            if (IsPointTouchingTarget(handPoint, handPointRadiusNormalized))
                return true;

            if (useForearmCapsuleHit &&
                activeLowerArm != null &&
                TryWorldToPosePoint(activeLowerArm.position, out Vector2 forearmPoint) &&
                IsSegmentTouchingTarget(forearmPoint, handPoint, handPointRadiusNormalized))
            {
                return true;
            }
        }

        return IsPoseHandTouchingTarget();
    }

    private bool IsPoseHandTouchingTarget()
    {
        bool activePoseVisible = _targetSide == TargetSide.Left
            ? poseManager.RightWristVisibility >= requiredVisibility || poseManager.RightElbowVisibility >= requiredVisibility
            : poseManager.LeftWristVisibility >= requiredVisibility || poseManager.LeftElbowVisibility >= requiredVisibility;

        if (!activePoseVisible)
            return false;

        Vector2 poseWrist = _targetSide == TargetSide.Left
            ? ToScreenPosePoint(poseManager.RightWrist)
            : ToScreenPosePoint(poseManager.LeftWrist);
        Vector2 poseElbow = _targetSide == TargetSide.Left
            ? ToScreenPosePoint(poseManager.RightElbow)
            : ToScreenPosePoint(poseManager.LeftElbow);

        return IsPointTouchingTarget(poseWrist, handPointRadiusNormalized) ||
               IsSegmentTouchingTarget(poseElbow, poseWrist, handPointRadiusNormalized);
    }

    private bool IsPointTouchingTarget(Vector2 point, float pointRadius)
    {
        return Vector2.Distance(point, _targetNormalizedPosition) <= targetHitRadiusNormalized + pointRadius;
    }

    private bool IsSegmentTouchingTarget(Vector2 a, Vector2 b, float capsuleRadius)
    {
        Vector2 closest = ClosestPointOnSegment(_targetNormalizedPosition, a, b);
        return IsPointTouchingTarget(closest, capsuleRadius);
    }

    private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 segment = b - a;
        float lengthSq = segment.sqrMagnitude;
        if (lengthSq <= 0.000001f)
            return a;

        float t = Vector2.Dot(point - a, segment) / lengthSq;
        return a + segment * Mathf.Clamp01(t);
    }

    private bool TryWorldToPosePoint(Vector3 worldPosition, out Vector2 point)
    {
        Camera cam = worldCamera != null ? worldCamera : Camera.main;
        if (cam == null)
        {
            point = Vector2.zero;
            return false;
        }

        Vector3 viewport = cam.WorldToViewportPoint(worldPosition);
        if (viewport.z < 0f)
        {
            point = Vector2.zero;
            return false;
        }

        point = new Vector2(viewport.x, 1f - viewport.y);
        return true;
    }

    private void ResolveCharacterReferences()
    {
        if (worldCamera == null)
            worldCamera = Camera.main;

        if (characterAnimator == null)
        {
            Animator[] animators = FindObjectsByType<Animator>(FindObjectsSortMode.None);
            foreach (Animator animator in animators)
            {
                if (animator != null && animator.avatar != null && animator.avatar.isHuman)
                {
                    characterAnimator = animator;
                    break;
                }
            }
        }

        if (characterAnimator == null)
            return;

        if (_leftHandBone == null)
            _leftHandBone = characterAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
        if (_rightHandBone == null)
            _rightHandBone = characterAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        if (_leftLowerArmBone == null)
            _leftLowerArmBone = characterAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        if (_rightLowerArmBone == null)
            _rightLowerArmBone = characterAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm);
    }

    private void UpdateDebugHandPoint()
    {
        if (_debugHandImage == null)
            return;

        if (!showDebugHandPoint || !_targetActive)
        {
            _debugHandImage.enabled = false;
            return;
        }

        ResolveCharacterReferences();
        Transform activeHand = _targetSide == TargetSide.Left ? _rightHandBone : _leftHandBone;
        if (activeHand == null || !TryWorldToPosePoint(activeHand.position, out Vector2 handPoint))
        {
            _debugHandImage.enabled = false;
            return;
        }

        RectTransform rt = _debugHandImage.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(handPoint.x, 1f - handPoint.y);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(28f, 28f);
        _debugHandImage.enabled = true;
    }

    private Vector2 ToScreenPosePoint(Vector3 landmark)
    {
        float x = mirrorInputX ? 1f - landmark.x : landmark.x;
        return new Vector2(x, landmark.y);
    }

    private void BuildHud()
    {
        GameObject canvasObject = new GameObject("PunchGameHUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        _canvas = canvasObject.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 20;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0.5f;

        Sprite targetSprite = CreateCircleSprite(128, new Color(1f, 0.16f, 0.08f, 0.92f));
        _targetImage = CreateImage("PunchTarget", canvasObject.transform, targetSprite);
        _targetImage.raycastTarget = false;

        Sprite debugSprite = CreateCircleSprite(32, new Color(0.05f, 1f, 0.35f, 0.95f));
        _debugHandImage = CreateImage("DetectedHandPoint", canvasObject.transform, debugSprite);
        _debugHandImage.raycastTarget = false;
        _debugHandImage.enabled = false;

        _scoreText = CreateText("ScoreText", canvasObject.transform, 42, TextAnchor.UpperLeft);
        RectTransform scoreRt = _scoreText.rectTransform;
        scoreRt.anchorMin = scoreRt.anchorMax = new Vector2(0f, 1f);
        scoreRt.pivot = new Vector2(0f, 1f);
        scoreRt.anchoredPosition = new Vector2(36f, -36f);
        scoreRt.sizeDelta = new Vector2(360f, 80f);

        _timerText = CreateText("TimerText", canvasObject.transform, 42, TextAnchor.UpperCenter);
        RectTransform timerRt = _timerText.rectTransform;
        timerRt.anchorMin = timerRt.anchorMax = new Vector2(0.5f, 1f);
        timerRt.pivot = new Vector2(0.5f, 1f);
        timerRt.anchoredPosition = new Vector2(0f, -36f);
        timerRt.sizeDelta = new Vector2(360f, 80f);

        _statusText = CreateText("StatusText", canvasObject.transform, 36, TextAnchor.LowerCenter);
        RectTransform statusRt = _statusText.rectTransform;
        statusRt.anchorMin = statusRt.anchorMax = new Vector2(0.5f, 0f);
        statusRt.pivot = new Vector2(0.5f, 0f);
        statusRt.anchoredPosition = new Vector2(0f, 92f);
        statusRt.sizeDelta = new Vector2(820f, 100f);
    }

    private void UpdateHud(string status)
    {
        if (_scoreText != null)
            _scoreText.text = $"Score: {_score}";

        if (_timerText != null)
            _timerText.text = $"Time: {Mathf.CeilToInt(Mathf.Max(0f, _timeRemaining))}";

        if (_statusText != null)
        {
            if (status != null)
                _statusText.text = status;
            else
                _statusText.text = _targetSide == TargetSide.Left ? "Right hand" : "Left hand";

            _statusText.enabled = true;
        }
    }

    private static Text CreateText(string name, Transform parent, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(parent, false);

        Text text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (text.font == null)
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static Image CreateImage(string name, Transform parent, Sprite sprite)
    {
        GameObject imageObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        Image image = imageObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        image.type = Image.Type.Simple;
        image.preserveAspect = true;
        return image;
    }

    private static Sprite CreateCircleSprite(int size, Color color)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "PunchCircle";

        float radius = (size - 2f) * 0.5f;
        Vector2 center = new Vector2((size - 1f) * 0.5f, (size - 1f) * 0.5f);
        Color clear = new Color(0f, 0f, 0f, 0f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                float alpha = Mathf.Clamp01(radius - distance);
                Color pixel = distance <= radius ? new Color(color.r, color.g, color.b, color.a * alpha) : clear;
                texture.SetPixel(x, y, pixel);
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}
