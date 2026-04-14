using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Manages the CalibrateScreen scene.
/// Uses PoseDetectionManager for both camera feed and landmark visibility.
///
/// Flow:
///   1. Show camera feed + 3 dots (head, left wrist, right wrist)
///   2. Dots turn green when landmarks are visible
///   3. All green for holdDurationSeconds → calibration complete
///   4. Saves calibrated T-pose directions for PoseToCharacter
///   5. Transitions to GameScene
/// </summary>
public class CalibrationManager : MonoBehaviour
{
    [Header("References — assign in Inspector")]
    public RawImage cameraBackground;
    public Image headDot;
    public Image leftWristDot;
    public Image rightWristDot;
    public Text instructionText;
    public AudioSource successAudio;
    public PoseDetectionManager poseManager;

    [Header("Settings")]
    public float visibilityThreshold = 0.7f;
    public float holdDurationSeconds  = 3.0f;
    public string nextSceneName       = "GameScene";

    private static readonly Color Red   = new Color(0.9f, 0.15f, 0.15f, 1f);
    private static readonly Color Green = new Color(0.15f, 0.85f, 0.25f, 1f);

    private float _allGreenTimer;
    private bool _calibrationDone;

    // ── Static storage for calibrated T-pose directions ──
    // Persists across scene loads so GameScene can use them.
    public static bool HasCalibrationData { get; private set; }
    public static Vector3 CalLeftUpperDir  { get; private set; }
    public static Vector3 CalLeftLowerDir  { get; private set; }
    public static Vector3 CalRightUpperDir { get; private set; }
    public static Vector3 CalRightLowerDir { get; private set; }

    private IEnumerator Start()
    {
        SetDotColor(headDot,       Red);
        SetDotColor(leftWristDot,  Red);
        SetDotColor(rightWristDot, Red);

        if (instructionText != null)
            instructionText.text = "Stand straight and show both hands to the camera";

        // Wait for PoseDetectionManager to start camera and load model
        if (poseManager == null)
        {
            Debug.LogError("[CalibrationManager] PoseManager not assigned!");
            yield break;
        }

        // Wait until camera texture is available
        yield return new WaitUntil(() => poseManager.CameraTexture != null);

        // Wire camera feed to background
        if (cameraBackground != null)
        {
            cameraBackground.texture = poseManager.CameraTexture;
            // Correct portrait rotation + front camera horizontal flip
            int angle = poseManager.CameraTexture.videoRotationAngle;
            bool mirrored = poseManager.CameraTexture.videoVerticallyMirrored;
            cameraBackground.rectTransform.localEulerAngles = new Vector3(0, 0, -angle);
            cameraBackground.rectTransform.localScale = new Vector3(mirrored ? -1 : 1, 1, 1);
        }

        // Wait for MediaPipe model to finish loading
        yield return new WaitUntil(() => poseManager.IsReady);
        Debug.Log("[CalibrationManager] PoseManager ready — starting calibration.");
    }

    private void Update()
    {
        if (_calibrationDone || poseManager == null || !poseManager.IsReady) return;

        bool headOk  = poseManager.Nose.z      >= visibilityThreshold;
        bool leftOk  = poseManager.LeftWrist.z >= visibilityThreshold;
        bool rightOk = poseManager.RightWrist.z >= visibilityThreshold;

        SetDotColor(headDot,       headOk  ? Green : Red);
        SetDotColor(leftWristDot,  leftOk  ? Green : Red);
        SetDotColor(rightWristDot, rightOk ? Green : Red);

        if (headOk && leftOk && rightOk)
        {
            _allGreenTimer += Time.deltaTime;
            if (instructionText != null)
                instructionText.text = $"Hold still... {Mathf.CeilToInt(holdDurationSeconds - _allGreenTimer + 1)}";

            if (_allGreenTimer >= holdDurationSeconds)
                StartCoroutine(OnCalibrationComplete());
        }
        else
        {
            _allGreenTimer = 0f;
            if (instructionText != null)
                instructionText.text = "Stand straight and show both hands to the camera";
        }
    }

    private IEnumerator OnCalibrationComplete()
    {
        _calibrationDone = true;

        // ── Capture T-pose landmark directions ──
        // Player is holding T-pose right now — save their actual arm directions
        // so PoseToCharacter can compute accurate deltas.
        CaptureTPoseDirections();

        if (instructionText != null)
            instructionText.text = "Great! Get ready...";

        if (successAudio != null && successAudio.clip != null)
        {
            successAudio.Play();
            yield return new WaitForSeconds(successAudio.clip.length);
        }
        else
        {
            yield return new WaitForSeconds(0.8f);
        }

        SceneManager.LoadScene(nextSceneName);
    }

    /// <summary>
    /// Captures the current landmark directions as T-pose reference.
    /// Stored in static fields so they survive the scene transition.
    /// </summary>
    private void CaptureTPoseDirections()
    {
        CalLeftUpperDir  = DirBetween(poseManager.LeftShoulder,  poseManager.LeftElbow);
        CalLeftLowerDir  = DirBetween(poseManager.LeftElbow,     poseManager.LeftWrist);
        CalRightUpperDir = DirBetween(poseManager.RightShoulder, poseManager.RightElbow);
        CalRightLowerDir = DirBetween(poseManager.RightElbow,    poseManager.RightWrist);
        HasCalibrationData = true;

        Debug.Log($"[CalibrationManager] T-pose captured: LU={CalLeftUpperDir:F2} RU={CalRightUpperDir:F2}");
    }

    private static Vector3 DirBetween(Vector3 from, Vector3 to)
    {
        float dx = to.x - from.x;
        float dy = -(to.y - from.y);
        return new Vector3(dx, dy, 0f).normalized;
    }

    private static void SetDotColor(Image dot, Color c)
    {
        if (dot != null) dot.color = c;
    }
}
