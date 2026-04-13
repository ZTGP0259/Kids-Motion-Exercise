using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Manages the CalibrateScreen scene.
/// Wires up camera feed, monitors MediaPipe landmark visibility,
/// turns calibration dots green, and transitions to GameScene on success.
/// </summary>
public class CalibrationManager : MonoBehaviour
{
    [Header("References — assign in Inspector")]
    public RawImage cameraBackground;       // Full-screen RawImage (bottom layer)
    public Image headDot;                   // Calibration dot: nose/head
    public Image leftWristDot;             // Calibration dot: left wrist
    public Image rightWristDot;            // Calibration dot: right wrist
    public Text instructionText;           // Bottom instruction label
    public AudioSource successAudio;       // AudioSource with success clip assigned
    public PoseDetectionManager poseManager; // Drag PoseManager GameObject here

    [Header("Settings")]
    public float visibilityThreshold = 0.7f;
    public float holdDurationSeconds  = 1.5f;
    public string nextSceneName       = "GameScene";

    // ── Colours ──
    private static readonly Color Red   = new Color(0.9f, 0.15f, 0.15f, 1f);
    private static readonly Color Green = new Color(0.15f, 0.85f, 0.25f, 1f);

    private WebCamTexture _webcam;
    private float _allGreenTimer;
    private bool _calibrationDone;

    private IEnumerator Start()
    {
        SetDotColor(headDot,       Red);
        SetDotColor(leftWristDot,  Red);
        SetDotColor(rightWristDot, Red);

        if (instructionText != null)
            instructionText.text = "Stand straight and show both hands to the camera";

        // Start camera
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("[CalibrationManager] Camera permission denied.");
            yield break;
        }

        WebCamDevice? backCam = null;
        foreach (var device in WebCamTexture.devices)
        {
            if (!device.isFrontFacing) { backCam = device; break; }
        }
        if (backCam == null) { Debug.LogError("[CalibrationManager] No back camera."); yield break; }

        _webcam = new WebCamTexture(backCam.Value.name, 640, 480, 30);
        _webcam.Play();
        yield return new WaitUntil(() => _webcam.width > 16);

        if (cameraBackground != null)
        {
            cameraBackground.texture = _webcam;
            // Fix portrait rotation from WebCamTexture
            cameraBackground.rectTransform.localEulerAngles = new Vector3(0, 0, -_webcam.videoRotationAngle);
        }
    }

    private void Update()
    {
        if (_calibrationDone || poseManager == null) return;

        bool headOk  = poseManager.Nose.z         >= visibilityThreshold;
        bool leftOk  = poseManager.LeftWrist.z    >= visibilityThreshold;
        bool rightOk = poseManager.RightWrist.z   >= visibilityThreshold;

        SetDotColor(headDot,       headOk  ? Green : Red);
        SetDotColor(leftWristDot,  leftOk  ? Green : Red);
        SetDotColor(rightWristDot, rightOk ? Green : Red);

        if (headOk && leftOk && rightOk)
        {
            _allGreenTimer += Time.deltaTime;
            if (_allGreenTimer >= holdDurationSeconds)
                StartCoroutine(OnCalibrationComplete());
        }
        else
        {
            _allGreenTimer = 0f;
        }
    }

    private IEnumerator OnCalibrationComplete()
    {
        _calibrationDone = true;

        if (instructionText != null)
            instructionText.text = "Great! Get ready...";

        if (successAudio != null)
        {
            successAudio.Play();
            yield return new WaitForSeconds(successAudio.clip != null ? successAudio.clip.length : 0.5f);
        }
        else
        {
            yield return new WaitForSeconds(0.5f);
        }

        SceneManager.LoadScene(nextSceneName);
    }

    private static void SetDotColor(Image dot, Color c)
    {
        if (dot != null) dot.color = c;
    }

    private void OnDestroy()
    {
        if (_webcam != null && _webcam.isPlaying) _webcam.Stop();
    }
}
