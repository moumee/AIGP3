using UnityEngine;

[RequireComponent(typeof(CombatCharacter))]
[RequireComponent(typeof(CooldownSystem))]
[RequireComponent(typeof(CombatActionController))]
public class StudentBTDefenderStrategy : MonoBehaviour
{
    [SerializeField] private CombatCharacter self;
    [SerializeField] private CombatCharacter target;
    [SerializeField] private CombatActionController actionController;
    [SerializeField] private CooldownSystem cooldownSystem;

    // UI 스크립트 연결용 변수 추가
    [Header("UI Debug")]
    [SerializeField] private AgentUI agentUI;

    [Header("Distance Settings")]
    [SerializeField] private float closeDistance = 1.6f;
    [SerializeField] private float attackDistance = 2.0f;
    [SerializeField] private float preferredDistance = 3.0f;

    [Header("Condition Settings")]
    [SerializeField] private float lowHealthRatio = 0.3f;

    private BTNode root;

    private void Awake()
    {
        FillDefaultReferences();
        BuildTree();
    }

    private void Reset()
    {
        FillDefaultReferences();
    }

    private void Update()
    {
        if (!CanTick()) return;

        // [핵심] 방어 중일 때는 트리를 틱(Tick)하지 않고 방어가 끝날 때까지 대기
        if (actionController.IsBlocking)
        {
            actionController.UpdateRotationLock(GetDirectionToTarget());
            return;
        }

        root.Tick();
    }

    private void BuildTree()
    {
        root = new SelectorNode(

            // [1] 긴급 회피 (생존 최우선)
            new SequenceNode(
                new ConditionNode(ShouldEmergencyDodge),
                new ActionNode(() => ExecuteActionWithUI("Emergency Dodge!", 5, DodgeAway))
            ),

            // [2] 방어 (상대가 2.0f 안에서 공격 모션을 취할 때)
            new SequenceNode(
                new ConditionNode(CanBlockIncomingAttack),
                new ActionNode(() => ExecuteActionWithUI("Blocking!", 4, Block))
            ),

            // [3] 카운터 공격을 회피 위로 올린다! (주먹이 쿨타임이면 이 노드는 실패하고 밑으로 넘어감)
            new SequenceNode(
                new ConditionNode(CanCounterAttack),
                new ParallelNode(1, 1,
                    new ActionNode(FaceTarget),
                    new ActionNode(() => ExecuteActionWithUI("Counter Attack!", 4, Attack))
                )
            ),

            // [4] 일반 회피를 공격 밑으로 내린다! (때릴 수 없을 때만 거리를 벌리며 도망감)
            new SequenceNode(
                new ConditionNode(CanDodgeCloseTarget),
                new ActionNode(() => ExecuteActionWithUI("Dodge!", 3, DodgeAway))
            ),

            // [5] 선호 거리 유지
            new DecoratorNode(
                new ActionNode(() => ExecuteActionWithUI("Maintain Distance", 1, MaintainDistance)),
                status => BTNodeStatus.Success
            )
        );
    }

    private bool CanTick()
    {
        return root != null
            && self != null
            && target != null
            && actionController != null
            && !self.IsDead
            && !target.IsDead;
    }

    // ==========================================
    // UI Helper Method
    // ==========================================
    // 상태 텍스트를 업데이트하고 해당 행동을 실행하는 헬퍼 메서드
    private BTNodeStatus ExecuteActionWithUI(string text, int priority, System.Func<BTNodeStatus> action)
    {
        if (agentUI != null)
        {
            // 글자 지속 시간은 0.5초로 통일 (원한다면 인자로 빼도 무방)
            agentUI.UpdateStatusText(text, 0.5f, priority);
        }
        return action.Invoke();
    }

    // ==========================================
    // Conditions
    // ==========================================
    private bool ShouldEmergencyDodge()
    {
        return self.CurrentHealthRatio <= lowHealthRatio
            && cooldownSystem != null
            && cooldownSystem.IsDodgeReady();
    }

    private bool CanBlockIncomingAttack()
    {
        return IsTargetWithinDistance(attackDistance)
            && cooldownSystem != null
            && cooldownSystem.IsBlockReady()
            && IsTargetAttacking(); // 확률 제거, 확실한 반응만!
    }
    private bool CanDodgeCloseTarget()
    {
        return IsTargetWithinDistance(closeDistance)
            && cooldownSystem != null
            && cooldownSystem.IsDodgeReady();
    }

    private bool CanCounterAttack()
    {
        // [수정] 상대가 공격 중이 아닐 때만 카운터를 날린다!
        // 공격형 AI가 공격을 끝내고 쿨타임이 도는 찰나를 노리는 전략입니다.
        return IsTargetWithinDistance(attackDistance)
            && cooldownSystem != null
            && cooldownSystem.IsAttackReady()
            && !IsTargetAttacking(); // 공격형 AI가 공격 중이 아닐 때 공격!
    }

    // ==========================================
    // Actions
    // ==========================================
    private BTNodeStatus Block()
    {
        actionController.Block(GetDirectionToTarget());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus DodgeAway()
    {
        actionController.Face(GetDirectionToTarget());
        actionController.Dodge(-GetDirectionToTarget());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus FaceTarget()
    {
        actionController.Face(GetDirectionToTarget());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus Attack()
    {
        actionController.Attack();
        return BTNodeStatus.Success;
    }

    private BTNodeStatus MaintainDistance()
    {
        Vector3 offset = GetHorizontalOffsetToTarget();
        float currentDist = offset.magnitude;

        if (currentDist < preferredDistance - 0.2f)
        {
            actionController.Move(-GetDirectionToTarget());
        }
        else if (currentDist > preferredDistance + 0.2f)
        {
            actionController.Move(GetDirectionToTarget());
        }
        else
        {
            actionController.Move(Vector3.zero);
        }

        return BTNodeStatus.Success;
    }

    // ==========================================
    // Helpers
    // ==========================================
    private bool IsTargetWithinDistance(float distance)
    {
        return GetHorizontalOffsetToTarget().magnitude <= distance;
    }

    private bool IsTargetAttacking()
    {
        CombatActionController targetAction = target.ActionController;
        return targetAction != null && targetAction.IsAttacking;
    }

    private bool IsTargetAttackReady()
    {
        CooldownSystem targetCooldown = target.CooldownSystem;
        return targetCooldown != null && targetCooldown.IsAttackReady();
    }

    private Vector3 GetDirectionToTarget()
    {
        Vector3 offset = GetHorizontalOffsetToTarget();
        return offset.sqrMagnitude <= 0.0001f ? transform.forward : offset.normalized;
    }

    private Vector3 GetHorizontalOffsetToTarget()
    {
        if (target == null) return transform.forward;

        Vector3 offset = target.transform.position - transform.position;
        offset.y = 0f;
        return offset;
    }

    private void FillDefaultReferences()
    {
        if (self == null) self = GetComponent<CombatCharacter>();
        if (actionController == null) actionController = GetComponent<CombatActionController>();
        if (cooldownSystem == null) cooldownSystem = GetComponent<CooldownSystem>();

        // AgentUI가 컴포넌트에 연결되어 있지 않다면 자식 오브젝트에서 자동으로 찾아오도록 추가
        if (agentUI == null) agentUI = GetComponentInChildren<AgentUI>();
    }
}