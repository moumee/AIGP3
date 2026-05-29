using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AgentUI : MonoBehaviour
{
    [SerializeField] private Image healthImage;
    [SerializeField] private TMP_Text statusText;
    
    private Camera _camera;
    private CombatCharacter _combatCharacter;

    private int _statusTextPriority = -1;
    private float _textEndTime;
    private void Awake()
    {
        _camera = Camera.main;
        _combatCharacter = GetComponentInParent<CombatCharacter>();
    }

    // Update is called once per frame
    void Update()
    {
        transform.rotation = _camera.transform.rotation;
        healthImage.fillAmount = _combatCharacter.CurrentHealthRatio;

        if (Time.time >= _textEndTime)
        {
            statusText.text = "";
            _statusTextPriority = -1;
        }
    }

    public void UpdateStatusText(string text, float duration, int priority)
    {
        if (priority < _statusTextPriority) return;

        _statusTextPriority = priority;
        _textEndTime = Time.time + duration;
        statusText.text = text;
    }
}
