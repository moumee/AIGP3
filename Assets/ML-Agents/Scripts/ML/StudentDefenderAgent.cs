using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

[RequireComponent(typeof(CombatCharacter))]
[RequireComponent(typeof(CooldownSystem))]
[RequireComponent(typeof(CombatActionController))]
public class StudentDefenderAgent : Agent
{
    public CombatCharacter self;
    public CombatCharacter opponent;
    public CombatActionController actionController;
    public CooldownSystem cooldownSystem;
    public EpisodeManager episodeManager;

    [Header("UI Debug")]
    public AgentUI agentUI; // UI 스크립트 연결용 변수 추가

    [Header("RL Reward Settings")]
    [SerializeField] private float preferredDistance = 3.0f;

    // PDF 가이드라인(25p)에 맞춘 2개의 Branch Action 정의
    private const int MoveNone = 0;
    private const int MoveForward = 1;
    private const int MoveBackward = 2;
    private const int MoveLeft = 3;
    private const int MoveRight = 4;

    private const int SkillNone = 0;
    private const int SkillAttack = 1;
    private const int SkillBlock = 2;
    private const int SkillDodge = 3;

    // 보상 계산을 위한 이전 체력 저장용 변수
    private float lastSelfHealthRatio;
    private float lastOpponentHealthRatio;

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
        // 매 에피소드 시작 시 체력 비율 초기화
        lastSelfHealthRatio = self.CurrentHealthRatio;
        lastOpponentHealthRatio = opponent.CurrentHealthRatio;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1~2. 양측 체력 비율 (2개)
        sensor.AddObservation(self.CurrentHealthRatio);
        sensor.AddObservation(opponent.CurrentHealthRatio);

        // 3~5. 거리 및 방향 정보 (3개)
        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;

        // 거리를 0~1 사이로 정규화 (최대 10유닛 기준)
        sensor.AddObservation(Mathf.Clamp01(distance / 10f));

        // 타겟 방향을 에이전트의 로컬 좌표계 기준으로 변환하여 관측
        Vector3 dir = distance > 0.001f ? offset.normalized : transform.forward;
        Vector3 localDir = transform.InverseTransformDirection(dir);
        sensor.AddObservation(localDir.x);
        sensor.AddObservation(localDir.z);

        // 6~8. 나의 쿨타임 상태 (3개)
        sensor.AddObservation(cooldownSystem.IsAttackReady() ? 1f : 0f);
        sensor.AddObservation(cooldownSystem.IsBlockReady() ? 1f : 0f);
        sensor.AddObservation(cooldownSystem.IsDodgeReady() ? 1f : 0f);

        // 9~11. 상대의 쿨타임 상태 (3개)
        sensor.AddObservation(opponent.CooldownSystem.IsAttackReady() ? 1f : 0f);
        sensor.AddObservation(opponent.CooldownSystem.IsBlockReady() ? 1f : 0f);
        sensor.AddObservation(opponent.CooldownSystem.IsDodgeReady() ? 1f : 0f);

        // 12~15. 현재 공격/방어 액션 상태 (4개)
        sensor.AddObservation(actionController.IsAttacking ? 1f : 0f);
        sensor.AddObservation(actionController.IsBlocking ? 1f : 0f);
        sensor.AddObservation(opponent.ActionController.IsAttacking ? 1f : 0f);
        sensor.AddObservation(opponent.ActionController.IsBlocking ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int moveAction = actions.DiscreteActions[0];
        int skillAction = actions.DiscreteActions[1];

        // 타겟을 항상 바라보도록 고정
        Vector3 directionToTarget = GetDirectionToTarget();
        actionController.Face(directionToTarget);

        // UI 조건 판단을 위한 현재 상태 계산
        float distance = GetHorizontalOffsetToTarget().magnitude;
        bool isOpponentAttacking = opponent.ActionController.IsAttacking;

        // 1. 이동 명령 수행 (Branch 0)
        Vector3 moveDir = Vector3.zero;
        switch (moveAction)
        {
            case MoveForward: moveDir = transform.forward; break;
            case MoveBackward: moveDir = -transform.forward; break;
            case MoveLeft: moveDir = -transform.right; break;
            case MoveRight: moveDir = transform.right; break;
        }
        actionController.Move(moveDir);

        // 스킬 없이 이동만 할 때의 UI 출력 (우선순위 1)
        if (skillAction == SkillNone && moveAction != MoveNone && agentUI != null)
        {
            agentUI.UpdateStatusText("Maintain Distance", 0.5f, 1);
        }

        // 2. 스킬 명령 수행 (Branch 1) 및 UI 출력
        switch (skillAction)
        {
            case SkillAttack:
                actionController.Attack();
                if (agentUI != null)
                {
                    // 상대가 공격 중이 아니고 거리가 가까울 때는 카운터 어택으로 판단
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
                    // 체력이 30% 이하일 때 회피하면 긴급 회피로 판단
                    if (self.CurrentHealthRatio <= 0.3f)
                        agentUI.UpdateStatusText("Emergency Dodge!", 0.5f, 5);
                    else
                        agentUI.UpdateStatusText("Dodge!", 0.5f, 3);
                }
                break;
        }

        // 3. 수비형(Defender) 전략에 맞춘 보상 부여
        AssignDefenderRewards(skillAction);
    }

    private void AssignDefenderRewards(int skillAction)
    {
        float distance = GetHorizontalOffsetToTarget().magnitude;
        bool isOpponentAttacking = opponent.ActionController.IsAttacking;

        if (distance >= preferredDistance - 0.5f && distance <= preferredDistance + 0.5f)
        {
            AddReward(0.001f);
        }
        else if (distance < 1.5f)
        {
            AddReward(-0.002f);
        }

        if (skillAction == SkillBlock)
        {
            if (isOpponentAttacking && distance <= 2.5f)
                AddReward(0.05f);
            else
                AddReward(-0.01f);
        }
        else if (skillAction == SkillDodge)
        {
            if (distance <= 2.0f)
                AddReward(0.03f);
            else
                AddReward(-0.01f);
        }

        // [핵심 전략 3] 카운터 공격 (허공 스윙 페널티 추가)
        if (skillAction == SkillAttack)
        {
            if (distance <= 2.2f)
            {
                if (!isOpponentAttacking)
                    AddReward(0.1f); // 유효 거리 내 빈틈 타격 시 큰 칭찬 (카운터)
                else
                    AddReward(-0.05f); // 유효 거리 내 난타전 맞불 시 페널티
            }
            else
            {
                AddReward(-0.02f); // [핵심 수정] 사거리 밖에서 허공에 헛스윙 시 페널티 부여
            }
        }

        float selfDamageTaken = lastSelfHealthRatio - self.CurrentHealthRatio;
        if (selfDamageTaken > 0)
        {
            AddReward(-selfDamageTaken * 1.0f);
        }
        lastSelfHealthRatio = self.CurrentHealthRatio;

        float oppDamageTaken = lastOpponentHealthRatio - opponent.CurrentHealthRatio;
        if (oppDamageTaken > 0)
        {
            AddReward(oppDamageTaken * 1.0f);
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
    }
}
        // AgentUI가 컴포넌트에 연결되어 있지 않다면 자식 오브젝트에서 자동으로 찾아오도록