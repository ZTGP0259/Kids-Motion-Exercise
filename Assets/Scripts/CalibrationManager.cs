using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Manages the CalibrateScreen scene.
/// Uses PoseDetectionManager for both camera feed and landmark visibility.
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
    public float holdDurationSeconds  = 1.5f;
    public string nextSceneName       = "GameScene";

    private static readonly Color Red   = new Color(0.9f, 0.15f, 0.15f, 1f);
    private static readonly Color Green = new Color(0.15f, 0.85f, 0.25f, 1f);

    private float _allGreenTimer;
    private bool _calibrationDone;

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

    private static void SetDotColor(Image dot, Color c)
    {
        if (dot != null) dot.color = c;
    }
}
