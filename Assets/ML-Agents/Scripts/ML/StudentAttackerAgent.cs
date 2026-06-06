using System;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

[RequireComponent(typeof(CombatCharacter))]
[RequireComponent(typeof(CooldownSystem))]
[RequireComponent(typeof(CombatActionController))]
public class StudentAttackerAgent : Agent
{
    public CombatCharacter self;
    public CombatCharacter opponent;
    public CombatActionController actionController;
    public CooldownSystem cooldownSystem;
    public EpisodeManager episodeManager;

    // Branch 0: Skill
    private const int SkillNone = 0;
    private const int SkillAttack = 1;
    private const int SkillBlock = 2;
    private const int SkillDodgeLeft = 3;
    private const int SkillDodgeRight = 4;

    // Branch 1: Movement
    private const int MoveNone = 0;
    private const int MoveApproach = 1;

    [SerializeField] private float maxObserveDistance = 10f;
    [SerializeField] private float dodgePositionOffset = 0.8f;

    [Header("Combat Geometry")]
    [Tooltip("공격이 닿는 거리. CombatHitDetector의 attackRange(2.0)와 맞춰야 함.")]
    [SerializeField] private float attackRange = 2.0f;

    [Tooltip("공격이 맞는 각도(절반). CombatHitDetector attackAngle 90의 절반인 45.")]
    [SerializeField] private float attackHalfAngle = 45f;

    [Tooltip("이 거리보다 가까우면 '너무 붙음'으로 간주(밀착 방지).")]
    [SerializeField] private float tooCloseDistance = 1.0f;

    [Header("Reward - Damage")]
    [Tooltip("상대에게 대미지를 줬을 때 보상(데미지 비율에 비례).")]
    [SerializeField] private float damageToOpponentReward = 1.5f;

    [Tooltip("내가 대미지를 받았을 때 패널티(데미지 비율에 비례).")]
    [SerializeField] private float damageToSelfPenalty = -0.7f;

    [Header("Reward - Attack Positioning (핵심)")]
    [Tooltip("사거리+각도가 맞는 좋은 위치에서 공격하면 주는 보상.")]
    [SerializeField] private float goodAttackReward = 0.2f;

    [Tooltip("사거리 밖이거나 각도가 안 맞는데 공격하면 주는 패널티(헛스윙 억제).")]
    [SerializeField] private float badAttackPenalty = -0.05f;

    [Header("Reward - Distance (밀착 방지)")]
    [Tooltip("때리기 좋은 거리(tooClose~attackRange)에 있으면 매 스텝 주는 보상.")]
    [SerializeField] private float goodDistanceReward = 0.005f;

    [Tooltip("사거리 밖에 있을 때 매 스텝 패널티(접근 유도).")]
    [SerializeField] private float farDistancePenalty = -0.005f;

    [Header("Reward - Skill Timing")]
    [Tooltip("상대 공격 중 회피 무적 진입 시 보상.")]
    [SerializeField] private float successfulDodgeReward = 0.3f;

    [Tooltip("상대 공격 중 블록으로 막으면 보상.")]
    [SerializeField] private float successfulBlockReward = 0.2f;

    [Tooltip("상대 block 쿨타임 중 공격 적중 시 추가 보상.")]
    [SerializeField] private float punishBaitedBlockReward = 0.4f;

    [Header("Reward - Penalty")]
    [Tooltip("매 스텝 작은 패널티(시간 끌기 억제).")]
    [SerializeField] private float stepPenalty = -0.001f;

    [Header("Reward - Episode End")]
    [Tooltip("내가 죽었을 때 패널티.")]
    [SerializeField] private float selfDeathPenalty = -1f;

    [Tooltip("상대를 죽였을 때 보상.")]
    [SerializeField] private float opponentDeathReward = 3f;

    [Tooltip("시간 초과 종료 시 패널티(결판 못 낸 무승부 억제).")]
    [SerializeField] private float episodeManagerEndReward = -0.5f;

    // 이전 프레임 상태
    private float _prevSelfHp;
    private float _prevTargetHp;
    private bool _prevTargetWasInvincible;
    private bool _prevSelfWasInvincible;
    private bool _waitingForEpisodeReset;

    private AgentUI _agentUI;
    private bool _shouldMove = false;

    public override void Initialize()
    {
        FillDefaultReferences();
    }

    private void Reset()
    {
        FillDefaultReferences();
    }

    public override void OnEpisodeBegin()
    {
        FillDefaultReferences();
        if (self == null || opponent == null) return;

        _prevSelfHp = self.CurrentHealthRatio;
        _prevTargetHp = opponent.CurrentHealthRatio;
        _prevTargetWasInvincible = false;
        _prevSelfWasInvincible = false;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 자신 (4)
        sensor.AddObservation(self.CurrentHealthRatio);
        sensor.AddObservation(cooldownSystem.IsAttackReady());
        sensor.AddObservation(cooldownSystem.IsBlockReady());
        sensor.AddObservation(cooldownSystem.IsDodgeReady());

        // 상대 (7)
        sensor.AddObservation(opponent.CurrentHealthRatio);
        sensor.AddObservation(opponent.ActionController.IsAttacking);
        sensor.AddObservation(opponent.ActionController.IsBlocking);
        sensor.AddObservation(opponent.ActionController.IsInvincible);
        sensor.AddObservation(opponent.CooldownSystem.IsBlockReady());
        sensor.AddObservation(opponent.CooldownSystem.IsDodgeReady());
        sensor.AddObservation(opponent.CooldownSystem.GetBlockCooldownRatio());

        // 상대적 위치 (2)
        float dist = Vector3.Distance(self.transform.position, opponent.transform.position);
        sensor.AddObservation(Mathf.Clamp01(dist / maxObserveDistance));
        sensor.AddObservation(Vector3.Dot(self.transform.forward, DirectionToTarget()));
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_waitingForEpisodeReset)
        {
            bool managerStillDone = episodeManager != null && episodeManager.IsEpisodeDone();
            bool someoneStillDead =
                (self != null && self.IsDead) ||
                (opponent != null && opponent.IsDead);

            if (managerStillDone || someoneStillDead)
            {
                return;
            }

            _waitingForEpisodeReset = false;
        }

        int skill = actions.DiscreteActions[0];
        int move = actions.DiscreteActions[1];

        float dist = Vector3.Distance(self.transform.position, opponent.transform.position);
        float angle = Vector3.Angle(self.transform.forward, DirectionToTarget());

        // ── 이동 ──────────────────────────────────
        if (move == MoveApproach)
        {
            _shouldMove = true;
            _agentUI.UpdateStatusText("Move Approach", 0.2f, 0);
        }
        else if (move == MoveNone)
        {
            _shouldMove = false;
        }

        // ── 스킬 ──────────────────────────────────
        switch (skill)
        {
            case SkillAttack:
                if (cooldownSystem.IsAttackReady())
                {
                    actionController.Face(DirectionToTarget());
                    actionController.Attack();
                    _agentUI.UpdateStatusText("Attack", 0.5f, 2);

                    // 좋은 위치(사거리+각도)에서 공격하면 보상, 아니면 패널티
                    if (dist <= attackRange && angle <= attackHalfAngle)
                    {
                        AddReward(goodAttackReward);
                    }
                    else
                    {
                        AddReward(badAttackPenalty);
                    }
                }
                break;

            case SkillBlock:
                if (cooldownSystem.IsBlockReady())
                {
                    actionController.Face(DirectionToTarget());
                    actionController.Block();
                    _agentUI.UpdateStatusText("Block", 0.5f, 2);
                }
                break;

            case SkillDodgeLeft:
                if (cooldownSystem.IsDodgeReady())
                {
                    Vector3 leftDir = Vector3.Cross(DirectionToTarget(), Vector3.up).normalized;
                    Vector3 leftPos = opponent.transform.position + leftDir * dodgePositionOffset;
                    Vector3 dodgeDir = (leftPos - self.transform.position).normalized;

                    actionController.Face(dodgeDir);
                    actionController.Dodge(dodgeDir);
                    _agentUI.UpdateStatusText("Left Dodge", 0.5f, 2);
                }
                break;

            case SkillDodgeRight:
                if (cooldownSystem.IsDodgeReady())
                {
                    Vector3 rightDir = Vector3.Cross(Vector3.up, DirectionToTarget()).normalized;
                    Vector3 rightPos = opponent.transform.position + rightDir * dodgePositionOffset;
                    Vector3 dodgeDir = (rightPos - self.transform.position).normalized;

                    actionController.Face(dodgeDir);
                    actionController.Dodge(dodgeDir);
                    _agentUI.UpdateStatusText("Right Dodge", 0.5f, 2);
                }
                break;
        }

        // ── Reward Shaping ─────────────────────────
        float curSelfHp = self.CurrentHealthRatio;
        float curTargetHp = opponent.CurrentHealthRatio;

        float selfDelta = curSelfHp - _prevSelfHp;
        float targetDelta = curTargetHp - _prevTargetHp;

        // 1. 데미지 주고받기 (양에 비례)
        if (targetDelta < 0f)
        {
            AddReward(-targetDelta * damageToOpponentReward);
        }
        if (selfDelta < 0f)
        {
            AddReward(selfDelta * damageToSelfPenalty * -1f);  // selfDelta 음수 → 패널티
        }

        // 2. 회피 성공
        if (opponent.ActionController.IsAttacking
            && self.ActionController.IsInvincible
            && !_prevSelfWasInvincible)
        {
            AddReward(successfulDodgeReward);
        }

        // 3. 블록 성공
        if (opponent.ActionController.IsAttacking
            && self.ActionController.IsBlocking
            && selfDelta == 0f)
        {
            AddReward(successfulBlockReward);
        }

        // 4. 빈틈 공격
        if (targetDelta < 0f && opponent.CooldownSystem.GetBlockCooldownRatio() > 0.35f)
        {
            AddReward(punishBaitedBlockReward);
        }

        // 5. 거리 보상 (밀착 방지 + 접근 유도)
        if (dist >= tooCloseDistance && dist <= attackRange)
        {
            AddReward(goodDistanceReward);    // 때리기 좋은 거리 유지
        }
        else if (dist > attackRange)
        {
            AddReward(farDistancePenalty);    // 멀면 접근 유도
        }
        // dist < tooCloseDistance (너무 붙음)은 보상 없음 → 밀착 억제

        // 6. 매 스텝 패널티
        AddReward(stepPenalty);

        // ── Episode 종료 ───────────────────────────
        if (self.IsDead)
        {
            FinishLearningEpisode(selfDeathPenalty);
            return;
        }
        if (opponent.IsDead)
        {
            FinishLearningEpisode(opponentDeathReward);
            return;
        }
        if (episodeManager != null && episodeManager.IsEpisodeDone())
        {
            FinishLearningEpisode(episodeManagerEndReward);
            return;
        }

        // 상태 갱신
        _prevSelfHp = curSelfHp;
        _prevTargetHp = curTargetHp;
        _prevTargetWasInvincible = opponent.ActionController.IsInvincible;
        _prevSelfWasInvincible = self.ActionController.IsInvincible;
    }

    private void FillDefaultReferences()
    {
        if (self == null) self = GetComponent<CombatCharacter>();
        if (actionController == null) actionController = GetComponent<CombatActionController>();
        if (cooldownSystem == null) cooldownSystem = GetComponent<CooldownSystem>();
        if (episodeManager == null) episodeManager = FindFirstObjectByType<EpisodeManager>();
        if (_agentUI == null) _agentUI = GetComponentInChildren<AgentUI>();
    }

    private Vector3 DirectionToTarget()
    {
        if (opponent == null) return transform.forward;
        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        return offset.sqrMagnitude <= 0.0001f ? transform.forward : offset.normalized;
    }

    private void FinishLearningEpisode(float finalReward)
    {
        AddReward(finalReward);
        _waitingForEpisodeReset = true;
        EndEpisode();
    }

    private void Update()
    {
        if (_shouldMove)
        {
            actionController.Move(DirectionToTarget());
        }
    }
}