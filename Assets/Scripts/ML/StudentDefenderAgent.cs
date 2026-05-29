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

    [Header("RL Reward Settings")]
    [SerializeField] private float preferredDistance = 3.0f;

    [Header("승/패 최종 보상 (Episode 종료 시)")]
    [Tooltip("상대를 쓰러뜨려 이겼을 때 주는 큰 보상")]
    [SerializeField] private float opponentDeathReward = 1f;
    [Tooltip("내가 쓰러져 졌을 때 주는 큰 페널티")]
    [SerializeField] private float selfDeathPenalty = -1f;
    [Tooltip("시간 초과로 끝났을 때 보상. 0=무승부. 방어형은 살짝 +를 주면 생존을 유도할 수 있으나, 너무 크면 시간만 끄는 행동을 학습함")]
    [SerializeField] private float timeoutReward = 0f;

    // PDF 가이드라인(25p)에 맞춘 2개의 Branch Action 정의
    // Branch 0: 이동 (0: 정지, 1: 전진, 2: 후퇴, 3: 좌이동, 4: 우이동)
    private const int MoveNone = 0;
    private const int MoveForward = 1;
    private const int MoveBackward = 2;
    private const int MoveLeft = 3;
    private const int MoveRight = 4;

    // Branch 1: 전투 행동 (0: 대기, 1: 공격, 2: 방어, 3: 회피)
    private const int SkillNone = 0;
    private const int SkillAttack = 1;
    private const int SkillBlock = 2;
    private const int SkillDodge = 3;

    // 보상 계산을 위한 이전 체력 저장용 변수
    private float lastSelfHealthRatio;
    private float lastOpponentHealthRatio;

    // 에피소드가 끝나고 리셋되는 동안 행동/보상 계산을 멈추기 위한 플래그
    private bool waitingForEpisodeReset;

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
        // [총 15개의 Observation] - PDF의 Space Size: 15와 반드시 일치해야 함

        // 1~2. 양측 체력 비율 (2개)
        sensor.AddObservation(self.CurrentHealthRatio);
        sensor.AddObservation(opponent.CurrentHealthRatio);

        // 3~5. 거리 및 방향 정보 (3개)
        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;

        // 거리를 0~1 사이로 정규화 (최대 10유닛 기준)
        sensor.AddObservation(Mathf.Clamp01(distance / 10f));

        // 타겟 방향을 에이전트의 로컬 좌표계 기준으로 변환하여 관측 (학습 효율 상승)
        Vector3 dir = distance > 0.001f ? offset.normalized : transform.forward;
        Vector3 localDir = transform.InverseTransformDirection(dir);
        sensor.AddObservation(localDir.x);
        sensor.AddObservation(localDir.z);

        // 6~8. 나의 쿨타임 상태 (3개) - 준비되었으면 1, 아니면 0
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
        // 직전 에피소드가 끝나 EpisodeManager가 캐릭터를 리셋하는 동안에는
        // 잘못된 상태(죽은 상태 등)로 행동/보상이 계산되지 않도록 잠시 멈춘다.
        if (waitingForEpisodeReset)
        {
            bool managerStillDone = episodeManager != null && episodeManager.IsEpisodeDone();
            bool someoneStillDead = (self != null && self.IsDead) || (opponent != null && opponent.IsDead);
            if (managerStillDone || someoneStillDead)
            {
                return;
            }
            waitingForEpisodeReset = false;
        }

        int moveAction = actions.DiscreteActions[0];
        int skillAction = actions.DiscreteActions[1];

        // 타겟을 항상 바라보도록 고정 (BT의 FaceTarget 역할)
        Vector3 directionToTarget = GetDirectionToTarget();
        actionController.Face(directionToTarget);

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

        // 2. 스킬 명령 수행 (Branch 1)
        switch (skillAction)
        {
            case SkillAttack:
                actionController.Attack();
                break;
            case SkillBlock:
                actionController.Block(directionToTarget);
                break;
            case SkillDodge:
                actionController.Dodge(-directionToTarget); // 뒤로 긴급 회피
                break;
        }

        // 3. 수비형(Defender) 전략에 맞춘 보상 부여
        AssignDefenderRewards(skillAction);

        // 4. 에피소드 종료 처리 + 승/패 최종 보상
        //    누가 죽거나 시간이 초과되면 결과에 맞는 보상을 주고 한 판을 끝낸다.
        //    EndEpisode()를 호출해야 ML-Agents가 "한 판 끝"을 인식하고 학습이 진행된다.
        if (self.IsDead)
        {
            FinishLearningEpisode(selfDeathPenalty);    // 내가 죽음 → 패배 (큰 페널티)
            return;
        }
        if (opponent.IsDead)
        {
            FinishLearningEpisode(opponentDeathReward); // 상대가 죽음 → 승리 (큰 보상)
            return;
        }
        if (episodeManager != null && episodeManager.IsEpisodeDone())
        {
            FinishLearningEpisode(timeoutReward);       // 시간 초과 → 무승부
            return;
        }
    }

    // 한 판(에피소드)을 마무리한다: 최종 보상을 더하고 ML-Agents에 종료를 알린다.
    private void FinishLearningEpisode(float finalReward)
    {
        AddReward(finalReward);
        waitingForEpisodeReset = true;
        EndEpisode();
    }

    private void AssignDefenderRewards(int skillAction)
    {
        float distance = GetHorizontalOffsetToTarget().magnitude;
        bool isOpponentAttacking = opponent.ActionController.IsAttacking;

        // [핵심 전략 1] 거리 유지 (BT의 MaintainDistance 모방)
        if (distance >= preferredDistance - 0.5f && distance <= preferredDistance + 0.5f)
        {
            AddReward(0.001f); // 선호 거리(3.0f) 유지 시 지속적인 칭찬
        }
        else if (distance < 1.5f)
        {
            AddReward(-0.002f); // 너무 가까이 붙으면 페널티 (아웃파이터 성향 부여)
        }

        // [핵심 전략 2] 수비 및 회피 판단 (BT의 CanBlockIncomingAttack, CanDodge 모방)
        if (skillAction == SkillBlock)
        {
            if (isOpponentAttacking && distance <= 2.5f)
                AddReward(0.05f); // 적의 공격 타이밍에 가드 올리면 칭찬
            else
                AddReward(-0.01f); // 허공에 가드 올리면 페널티
        }
        else if (skillAction == SkillDodge)
        {
            if (distance <= 2.0f)
                AddReward(0.03f); // 적이 가까울 때 거리를 벌리는 회피는 칭찬
            else
                AddReward(-0.01f); // 멀리서 의미 없이 구르면 페널티
        }

        // [핵심 전략 3] 카운터 공격 (BT의 CanCounterAttack 모방)
        if (skillAction == SkillAttack)
        {
            if (distance <= 2.2f && !isOpponentAttacking)
                AddReward(0.1f); // 적이 공격 중이 아닌 빈틈을 타격하면 큰 칭찬 (카운터)
            else if (isOpponentAttacking)
                AddReward(-0.05f); // 적이 공격 중인데 난타전 맞불을 놓으면 페널티
        }

        // [핵심 전략 4] 실제 체력 증감에 따른 절대적 보상/페널티
        float selfDamageTaken = lastSelfHealthRatio - self.CurrentHealthRatio;
        if (selfDamageTaken > 0)
        {
            AddReward(-selfDamageTaken * 1.0f); // 내가 데미지를 입으면 큰 페널티
        }
        lastSelfHealthRatio = self.CurrentHealthRatio;

        float oppDamageTaken = lastOpponentHealthRatio - opponent.CurrentHealthRatio;
        if (oppDamageTaken > 0)
        {
            AddReward(oppDamageTaken * 1.0f); // 상대에게 데미지를 주면 큰 보상
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