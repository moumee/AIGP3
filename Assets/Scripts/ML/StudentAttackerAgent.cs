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

    // Action constants for a clear RL action space.
    // Branch 0: action type (0 = none, 1 = attack, 2 = block, 3 = dodge)
    // Add more branches if more complex behaviors are needed.
    // Make sure the Behavior Parameters action space in Unity Editor matches these constants.
    
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

    [Header("Reward Tuning - Damage Result")]
    [Tooltip("상대에게 대미지를 줬을 때 얻는 보상 배율입니다. 값을 키우면 Agent가 공격 성공을 더 중요하게 학습합니다.")]
    [SerializeField] private float damageToOpponentRewardMultiplier = 2.0f;

    [Tooltip("내가 대미지를 받았을 때 받는 패널티 배율입니다. 값을 키우면 Agent가 피격을 더 강하게 회피하도록 학습합니다.")] 
    [SerializeField] private float damageToSelfPenaltyMultiplier = 1.5f;

    [Header("Reward Tuning - Skill Timing")]
    [Tooltip("상대가 공격 중일 때 dodge 무적 프레임에 진입하면 주는 보상입니다. 값을 키우면 회피 타이밍을 더 중요하게 학습합니다.")]
    [SerializeField] private float successfulDodgeReward = 0.3f;

    [Tooltip("상대가 공격 중일 때 block 상태로 피해를 막으면 주는 보상입니다. 값을 키우면 방어 행동을 더 선호합니다.")] 
    [SerializeField] private float successfulBlockReward = 0.2f;

    [Tooltip("상대 block이 쿨타임일 때 공격을 맞추면 주는 추가 보상입니다. 값을 키우면 빈틈을 노리는 공격을 더 선호합니다.")] 
    [SerializeField] private float punishBaitedBlockReward = 0.4f;

    [Tooltip("상대가 dodge를 끝낸 직후 dodge로 추격하면 주는 보상입니다. 값을 키우면 상대 회피 후 따라붙는 행동을 더 선호합니다.")]
    [SerializeField] private float followDodgeReward = 0.2f;

    [Header("Reward Tuning - Penalty")]
    [Tooltip("상대 block이 쿨타임이고 공격 거리 안인데 공격하지 않았을 때 받는 패널티입니다. 더 음수로 만들면 기회 낭비를 더 강하게 벌합니다.")]
    [SerializeField] private float missedPunishPenalty = -0.05f;

    [Tooltip("매 스텝마다 받는 작은 패널티입니다. 더 음수로 만들면 가만히 있거나 시간을 끄는 행동을 줄일 수 있습니다.")] 
    [SerializeField] private float stepPenalty = -0.001f;

    [Header("Reward Tuning - Episode End")]
    [Tooltip("내가 죽었을 때 받는 최종 패널티입니다. 더 음수로 만들면 생존을 더 중요하게 학습합니다.")]
    [SerializeField] private float selfDeathPenalty = -1f;

    [Tooltip("상대를 죽였을 때 받는 최종 보상입니다. 값을 키우면 승리를 더 중요하게 학습합니다.")] 
    [SerializeField] private float opponentDeathReward = 1f;

    [Tooltip("EpisodeManager가 시간 초과 등으로 라운드를 끝냈을 때 주는 보상입니다. 0이면 무승부, 음수면 시간 끌기를 벌합니다.")] 
    [SerializeField] private float episodeManagerEndReward = 0f;

    // 이전 프레임 상태 추적 (reward 계산용)
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
        // TODO: Reset or initialize values needed at the start of each episode.
        _prevSelfHp = self.CurrentHealthRatio;
        _prevTargetHp = opponent.CurrentHealthRatio;
        _prevTargetWasInvincible = false;
        _prevSelfWasInvincible = false;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // TODO: Add observations for the agent to learn from.
        // Example:
        // sensor.AddObservation(self.CurrentHealthRatio);
        // Make sure the Behavior Parameters observation space in Unity Editor matches the number of observations added here.
        
        // 자신 (4)
        sensor.AddObservation(self.CurrentHealthRatio);
        sensor.AddObservation(cooldownSystem.IsAttackReady());
        sensor.AddObservation(cooldownSystem.IsBlockReady());
        sensor.AddObservation(cooldownSystem.IsDodgeReady());

        // 상대 (7) — BT의 ConditionNode들이 참조하는 값
        sensor.AddObservation(opponent.CurrentHealthRatio);
        sensor.AddObservation(opponent.ActionController.IsAttacking);
        sensor.AddObservation(opponent.ActionController.IsBlocking);
        sensor.AddObservation(opponent.ActionController.IsInvincible);
        sensor.AddObservation(opponent.CooldownSystem.IsBlockReady());
        sensor.AddObservation(opponent.CooldownSystem.IsDodgeReady());
        sensor.AddObservation(opponent.CooldownSystem.GetBlockCooldownRatio()); // PunishBaitedBlock 핵심 신호

        // 상대적 위치 (2)
        float dist = Vector3.Distance(self.transform.position, opponent.transform.position);
        sensor.AddObservation(Mathf.Clamp01(dist / maxObserveDistance));
        sensor.AddObservation(Vector3.Dot(self.transform.forward, DirectionToTarget())); // Facing 각도
        
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // TODO: Convert actions into movement or combat commands.
        // TODO: Add rewards or penalties based on the result.
        // TODO: End the episode when needed.

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

        // ── 이동 ──────────────────────────────────
        // BT의 ApproachTarget / BlockBait 이동에 대응
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
            // BT: Attack (PunishBaitedBlock / FollowDodge 이후 공격)
            case SkillAttack:
                if (cooldownSystem.IsAttackReady())
                {
                    actionController.Face(DirectionToTarget());
                    actionController.Attack();
                    _agentUI.UpdateStatusText("Attack", 0.5f, 2);
                }
                break;

            // BT: ShouldBlock → Block
            case SkillBlock:
                if (cooldownSystem.IsBlockReady())
                {
                    actionController.Face(DirectionToTarget());
                    actionController.Block();
                    _agentUI.UpdateStatusText("Block", 0.5f, 2);
                }
                break;

            // BT: RandomSelectorNode → LeftDodge
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

            // BT: RandomSelectorNode → RightDodge
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

        // 1. 기본 대미지 주고받기
        if (targetDelta < 0f)
        {
            AddReward(-targetDelta * damageToOpponentRewardMultiplier);   // 상대에게 대미지 줌  (+)
        }

        if (selfDelta < 0f)
        {
            AddReward(selfDelta * damageToSelfPenaltyMultiplier);         // 내가 대미지 받음    (-)
        }

        // 2. BT: ShouldDodge → LeftDodge / RightDodge
        // 상대가 공격 중인 타이밍에 dodge 무적 프레임 진입하면 보상
        if (opponent.ActionController.IsAttacking
            && self.ActionController.IsInvincible
            && !_prevSelfWasInvincible)
        {
            AddReward(successfulDodgeReward);
        }

        // 3. BT: ShouldBlock → Block
        // 상대 공격 중에 블록하고 피격을 막으면 보상
        if (opponent.ActionController.IsAttacking
            && self.ActionController.IsBlocking
            && selfDelta == 0f)
        {
            AddReward(successfulBlockReward);
        }

        // 4. BT: ShouldPunishBaitedBlock → PunishBaitedBlock
        // 상대 block cooldown 중(>0.35)에 공격이 맞으면 추가 보상
        if (targetDelta < 0f && opponent.CooldownSystem.GetBlockCooldownRatio() > 0.35f)
        {
            AddReward(punishBaitedBlockReward);
        }

        // 5. BT: TransitToFollowDodge → FollowDodge
        // 상대가 방금 dodge를 끝낸 직후 내가 dodge로 추격하면 보상
        bool targetJustFinishedDodge = _prevTargetWasInvincible && !opponent.ActionController.IsInvincible;

        if (targetJustFinishedDodge && (skill == SkillDodgeLeft || skill == SkillDodgeRight))
        {
            AddReward(followDodgeReward);
        }

        // 6. BT: BlockBait 기회 낭비 패널티
        // 상대 block이 cooldown 중이고 공격 거리 안인데 공격 안 하면 소폭 패널티
        float dist = Vector3.Distance(self.transform.position, opponent.transform.position);

        if (!opponent.CooldownSystem.IsBlockReady()
            && dist < 2f
            && skill != SkillAttack)
        {
            AddReward(missedPunishPenalty);
        }

        // 7. 소극적 플레이 방지 (매 스텝 소량 패널티)
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

        // 이전 프레임 상태 갱신
        _prevSelfHp = curSelfHp;
        _prevTargetHp = curTargetHp;
        _prevTargetWasInvincible = opponent.ActionController.IsInvincible;
        _prevSelfWasInvincible = self.ActionController.IsInvincible;
    }

    private void FillDefaultReferences()
    {
        if (self == null)
        {
            self = GetComponent<CombatCharacter>();
        }

        if (actionController == null)
        {
            actionController = GetComponent<CombatActionController>();
        }

        if (cooldownSystem == null)
        {
            cooldownSystem = GetComponent<CooldownSystem>();
        }

        if (episodeManager == null)
        {
            episodeManager = FindFirstObjectByType<EpisodeManager>();
        }

        if (_agentUI == null)
        {
            _agentUI = GetComponentInChildren<AgentUI>();
        }
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
