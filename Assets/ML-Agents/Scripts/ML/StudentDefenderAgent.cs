using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

[RequireComponent(typeof(CombatCharacter))]
[RequireComponent(typeof(CooldownSystem))]
[RequireComponent(typeof(CombatActionController))]
public class StudentDefenderAgent : Agent
{
    // [컴포넌트 참조] 에이전트와 타겟, 스킬 및 상태 관리를 위한 클래스 연결
    public CombatCharacter self;
    public CombatCharacter opponent;
    public CombatActionController actionController;
    public CooldownSystem cooldownSystem;
    public EpisodeManager episodeManager;

    [Header("UI Debug")]
    // 에이전트의 현재 행동을 머리 위에 텍스트로 띄워주기 위한 UI 스크립트
    public AgentUI agentUI;

    // 거리 유지(preferredDistance) 관련 변수 완전 삭제됨

    [Header("RL Reward Settings - Defense")]
    // [보상 파라미터 - 수비/회피]
    [SerializeField] private float rewardSuccessfulBlock = 0.05f; // 적의 공격 타이밍에 맞춰 가드를 올렸을 때의 보상
    [SerializeField] private float penaltyEmptyBlock = -0.01f; // 적이 때리지도 않는데 허공에 가드를 올릴 때의 감점
    [SerializeField] private float rewardEffectiveDodge = 0.03f; // 적이 가까울 때 적절히 거리를 벌리는 회피 보상
    [SerializeField] private float penaltyMeaninglessDodge = -0.01f; // 멀리서 의미 없이 구를 때의 감점

    [Header("RL Reward Settings - Attack")]
    // [보상 파라미터 - 공격]
    [SerializeField] private float rewardCounterAttack = 0.1f; // 적의 빈틈(공격 중이 아닐 때)을 정확히 타격한 카운터 보상
    [SerializeField] private float penaltyBrawl = -0.05f; // 적이 공격 중일 때 같이 주먹을 뻗는(난타전) 무모한 행동에 대한 감점
    [SerializeField] private float penaltyAirSwing = -0.02f; // 사거리 밖에서 헛스윙을 할 때의 감점

    [Header("RL Reward Settings - Health")]
    // [보상 파라미터 - 체력] 실제 체력 증감에 따른 최종적인 승패 보상 배율
    [SerializeField] private float selfDamagePenaltyMultiplier = 1.0f;
    [SerializeField] private float opponentDamageRewardMultiplier = 1.0f;

    // [행동 정의] PDF 가이드라인(25p)에 맞춘 2개의 Branch Action
    private const int MoveNone = 0;
    private const int MoveForward = 1;
    private const int MoveBackward = 2;
    private const int MoveLeft = 3;
    private const int MoveRight = 4;

    private const int SkillNone = 0;
    private const int SkillAttack = 1;
    private const int SkillBlock = 2;
    private const int SkillDodge = 3;

    // [상태 저장용 변수] 체력 변화를 감지하여 보상을 주기 위해 '이전 스텝의 체력'을 기억하는 변수
    private float lastSelfHealthRatio;
    private float lastOpponentHealthRatio;

    // 에이전트가 처음 생성될 때 1회 호출됨
    public override void Initialize()
    {
        FillDefaultReferences();
    }

    private void Reset()
    {
        FillDefaultReferences();
    }

    // 매 에피소드(라운드)가 새로 시작될 때마다 호출됨
    public override void OnEpisodeBegin()
    {
        lastSelfHealthRatio = self.CurrentHealthRatio;
        lastOpponentHealthRatio = opponent.CurrentHealthRatio;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(self.CurrentHealthRatio);
        sensor.AddObservation(opponent.CurrentHealthRatio);

        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;

        sensor.AddObservation(Mathf.Clamp01(distance / 10f));

        Vector3 dir = distance > 0.001f ? offset.normalized : transform.forward;
        Vector3 localDir = transform.InverseTransformDirection(dir);
        sensor.AddObservation(localDir.x);
        sensor.AddObservation(localDir.z);

        sensor.AddObservation(cooldownSystem.IsAttackReady() ? 1f : 0f);
        sensor.AddObservation(cooldownSystem.IsBlockReady() ? 1f : 0f);
        sensor.AddObservation(cooldownSystem.IsDodgeReady() ? 1f : 0f);

        sensor.AddObservation(opponent.CooldownSystem.IsAttackReady() ? 1f : 0f);
        sensor.AddObservation(opponent.CooldownSystem.IsBlockReady() ? 1f : 0f);
        sensor.AddObservation(opponent.CooldownSystem.IsDodgeReady() ? 1f : 0f);

        sensor.AddObservation(actionController.IsAttacking ? 1f : 0f);
        sensor.AddObservation(actionController.IsBlocking ? 1f : 0f);
        sensor.AddObservation(opponent.ActionController.IsAttacking ? 1f : 0f);
        sensor.AddObservation(opponent.ActionController.IsBlocking ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int moveAction = actions.DiscreteActions[0];
        int skillAction = actions.DiscreteActions[1];

        Vector3 directionToTarget = GetDirectionToTarget();
        actionController.Face(directionToTarget);

        float distance = GetHorizontalOffsetToTarget().magnitude;
        bool isOpponentAttacking = opponent.ActionController.IsAttacking;

        Vector3 moveDir = Vector3.zero;
        switch (moveAction)
        {
            case MoveForward: moveDir = transform.forward; break;
            case MoveBackward: moveDir = -transform.forward; break;
            case MoveLeft: moveDir = -transform.right; break;
            case MoveRight: moveDir = transform.right; break;
        }
        actionController.Move(moveDir);

        if (skillAction == SkillNone && moveAction != MoveNone && agentUI != null)
        {
            agentUI.UpdateStatusText("Maintain Distance", 0.5f, 1);
        }

        switch (skillAction)
        {
            case SkillAttack:
                actionController.Attack();
                if (agentUI != null)
                {
                    if (distance <= 2.2f && !isOpponentAttacking)
                        agentUI.UpdateStatusText("Counter Attack!", 0.5f, 4);
                    else
                        agentUI.UpdateStatusText("Attack!", 0.5f, 3);
                }
                break;

            case SkillBlock:
                actionController.Block(directionToTarget);
                if (agentUI != null)
                {
                    agentUI.UpdateStatusText("Blocking!", 0.5f, 4);
                }
                break;

            case SkillDodge:
                actionController.Dodge(-directionToTarget);
                if (agentUI != null)
                {
                    if (self.CurrentHealthRatio <= 0.3f)
                        agentUI.UpdateStatusText("Emergency Dodge!", 0.5f, 5);
                    else
                        agentUI.UpdateStatusText("Dodge!", 0.5f, 3);
                }
                break;
        }

        AssignDefenderRewards(skillAction);
    }

    private void AssignDefenderRewards(int skillAction)
    {
        float distance = GetHorizontalOffsetToTarget().magnitude;
        bool isOpponentAttacking = opponent.ActionController.IsAttacking;

        // [전략 1] 거리 유지 로직 완전 삭제됨

        // [핵심 전략 2] 방어 및 회피 타이밍 학습
        if (skillAction == SkillBlock)
        {
            if (isOpponentAttacking && distance <= 2.5f)
                AddReward(rewardSuccessfulBlock);
            else
                AddReward(penaltyEmptyBlock);
        }
        else if (skillAction == SkillDodge)
        {
            if (distance <= 2.0f)
                AddReward(rewardEffectiveDodge);
            else
                AddReward(penaltyMeaninglessDodge);
        }

        // [핵심 전략 3] 스마트한 공격 (카운터 유도 및 헛스윙 방지)
        if (skillAction == SkillAttack)
        {
            if (distance <= 2.2f)
            {
                if (!isOpponentAttacking)
                    AddReward(rewardCounterAttack);
                else
                    AddReward(penaltyBrawl);
            }
            else
            {
                AddReward(penaltyAirSwing);
            }
        }

        // [핵심 전략 4] 실제 체력 증감에 따른 절대적 결과 보상
        float selfDamageTaken = lastSelfHealthRatio - self.CurrentHealthRatio;
        if (selfDamageTaken > 0)
        {
            AddReward(-selfDamageTaken * selfDamagePenaltyMultiplier);
        }
        lastSelfHealthRatio = self.CurrentHealthRatio;

        float oppDamageTaken = lastOpponentHealthRatio - opponent.CurrentHealthRatio;
        if (oppDamageTaken > 0)
        {
            AddReward(oppDamageTaken * opponentDamageRewardMultiplier);
        }
        lastOpponentHealthRatio = opponent.CurrentHealthRatio;
    }

    private Vector3 GetDirectionToTarget()
    {
        Vector3 offset = GetHorizontalOffsetToTarget();
        return offset.sqrMagnitude <= 0.0001f ? transform.forward : offset.normalized;
    }

    private Vector3 GetHorizontalOffsetToTarget()
    {
        if (opponent == null) return transform.forward;
        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        return offset;
    }

    private void FillDefaultReferences()
    {
        if (self == null) self = GetComponent<CombatCharacter>();
        if (actionController == null) actionController = GetComponent<CombatActionController>();
        if (cooldownSystem == null) cooldownSystem = GetComponent<CooldownSystem>();
        if (episodeManager == null) episodeManager = FindFirstObjectByType<EpisodeManager>();

        if (agentUI == null) agentUI = GetComponentInChildren<AgentUI>();
    }
}