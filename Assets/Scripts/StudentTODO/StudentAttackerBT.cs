using UnityEngine;

// Student template: replace BuildTree() with an attacker or defender BT strategy.
public class StudentAttackerBT: MonoBehaviour
{
    [SerializeField] private CombatCharacter self;
    [SerializeField] private CombatCharacter target;
    [SerializeField] private CombatActionController actionController;
    [SerializeField] private CooldownSystem cooldownSystem;

    [Header("Dodge")]
    [SerializeField] private float dodgeTriggerDistance = 1.6f;
    [SerializeField] private float dodgePositionOffset = 0.8f;
    
    [Header("Block")]
    [SerializeField] private float blockTriggerDistance = 1.6f;

    [Header("Block Bait")] 
    [SerializeField] private float blockBaitApproachDistance = 1.6f;
    
    [SerializeField] private float attackDistance = 2f;

    private AgentUI _agentUI;

    private BTNode root;

    private void Awake()
    {
        FillDefaultReferences();
        BuildTree();
        
        _agentUI = GetComponentInChildren<AgentUI>();
    }

    private void Reset()
    {
        FillDefaultReferences();
    }

    private void Update()
    {
        if (!CanTick())
        {
            return;
        }

        root.Tick();
    }

    private void BuildTree()
    {
        // TODO: Choose an attacker or defender role.
        // TODO: Build a root SelectorNode or SequenceNode.
        // TODO: Add ConditionNode objects for health, distance, cooldown, and facing checks.
        // TODO: Add ActionNode objects that call only:
        // actionController.Move(direction), Attack(), Block(), or Dodge(direction).
        // TODO: Include at least two advanced elements in your final strategy:
        // DecoratorNode, ParallelNode, RandomSelectorNode, or another non-deterministic choice.
        root = new SelectorNode(
            new SequenceNode(
                new ConditionNode(ShouldDodge),
                new RandomSelectorNode(
                    new ActionNode(LeftDodge),
                    new ActionNode(RightDodge))),
            new SequenceNode(
                new ConditionNode(ShouldBlock),
                new ActionNode(Block)),
            new SequenceNode(
                new ConditionNode(ShouldPunishBaitedBlock),
                new ActionNode(PunishBaitedBlock)),
            new SequenceNode(
                new ConditionNode(ShouldBlockBait),
                new DecoratorNode(new ActionNode(BlockBait), TransitToFollowDodge)),
            new SequenceNode(
                new ConditionNode(CanFollowDodge),
                new ActionNode(FollowDodge)),
            new ActionNode(ApproachTarget));
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

    private Vector3 DirectionToTarget()
    {
        if (target == null)
        {
            return transform.forward;
        }

        Vector3 offset = target.transform.position - transform.position;
        offset.y = 0f;
        return offset.sqrMagnitude <= 0.0001f ? transform.forward : offset.normalized;
    }

    private float DistanceToTarget()
    {
        if (target == null)
        {
            return float.MaxValue;
        }

        Vector3 offset = target.transform.position - transform.position;
        offset.y = 0f;
        return offset.magnitude;
    }

    private bool IsFacingTarget(float maxAngle)
    {
        Vector3 direction = DirectionToTarget();
        Vector3 forward = transform.forward;
        forward.y = 0f;
        return Vector3.Angle(forward, direction) <= maxAngle;
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
    }

    private bool IsTargetOpenForAttack()
    {
        return target.CooldownSystem != null
               && !target.CooldownSystem.IsBlockReady()
               && !target.CooldownSystem.IsDodgeReady()
               && !target.ActionController.IsInvincible
               && !target.ActionController.IsBlocking;
    }

    private bool ShouldDodge()
    {
        return target.ActionController.IsAttacking
               && DistanceToTarget() <= dodgeTriggerDistance
               && cooldownSystem != null
               && cooldownSystem.IsDodgeReady();
    }

    private BTNodeStatus LeftDodge()
    {
        Vector3 leftOffsetPos = target.transform.position +
                                Vector3.Cross(DirectionToTarget(), Vector3.up).normalized * dodgePositionOffset;
        Vector3 direction = (leftOffsetPos - self.transform.position).normalized;

        actionController.Face(direction.magnitude < 0.0001f ? self.transform.forward : direction);
        actionController.Dodge(direction.magnitude < 0.0001f ? self.transform.forward : direction);

        _agentUI.UpdateStatusText("Left Dodge", 0.5f, 2);

        return BTNodeStatus.Success;
    }

    private BTNodeStatus RightDodge()
    {
        Vector3 rightOffsetPos = target.transform.position +
                                 Vector3.Cross(Vector3.up, DirectionToTarget()).normalized * dodgePositionOffset;
        Vector3 direction = (rightOffsetPos - self.transform.position).normalized;

        actionController.Face(direction.magnitude < 0.0001f ? self.transform.forward : direction);
        actionController.Dodge(direction.magnitude < 0.0001f ? self.transform.forward : direction);

        _agentUI.UpdateStatusText("Right Dodge", 0.5f, 2);

        return BTNodeStatus.Success;
    }

    private bool ShouldBlock()
    {
        return target.ActionController.IsAttacking
               && DistanceToTarget() <= blockTriggerDistance
               && cooldownSystem != null
               && cooldownSystem.IsBlockReady();
    }

    private BTNodeStatus Block()
    {
        actionController.Face(DirectionToTarget());
        actionController.Block();
        
        _agentUI.UpdateStatusText("Block", 0.5f, 2);
        
        return BTNodeStatus.Success;
    }

    // 상대가 속아서 막기를 사용한 뒤 아직 막기 쿨타임이 도는 중인 경우 공격하기
    private bool ShouldPunishBaitedBlock()
    {
        return DistanceToTarget() < attackDistance
               && cooldownSystem != null
               && cooldownSystem.IsAttackReady()
               && IsTargetOpenForAttack()
               && target.CooldownSystem.GetBlockCooldownRatio() > 0.35f;
    }

    private BTNodeStatus PunishBaitedBlock()
    {
        actionController.Face(DirectionToTarget());
        actionController.Attack();
        _agentUI.UpdateStatusText("Punish Baited Block", 0.5f, 2);
        return BTNodeStatus.Success;
    }

    private bool ShouldBlockBait()
    {
        return target.CooldownSystem.IsBlockReady();
    }

    private BTNodeStatus TransitToFollowDodge(BTNodeStatus status)
    {
        // BlockBait 액션 노드로 접근 중에 상대가 Dodge를 했다면 노드를 Failure를 반환하게 하여 바로 뒤에 있는 FollowDodge 시퀀스를 
        // 시도하게 하는 데코레이터
        if (status == BTNodeStatus.Success)
        {
            if (target.ActionController.IsInvincible)
            {
                return BTNodeStatus.Failure;
            }
        }

        return status;
    }

    private BTNodeStatus BlockBait()
    {
        if (DistanceToTarget() >= blockBaitApproachDistance)
        {
            actionController.Move(DirectionToTarget());
        }

        _agentUI.UpdateStatusText("Block Bait", 0.2f, 1);
        
        return BTNodeStatus.Success;
    }

    private bool CanFollowDodge()
    {
        return cooldownSystem.IsDodgeReady();
    }
    
    private BTNodeStatus FollowDodge()
    {
        actionController.Face(DirectionToTarget());
        actionController.Dodge(DirectionToTarget());
        _agentUI.UpdateStatusText("Follow Dodge", 0.5f, 2);
        return BTNodeStatus.Success;
    }

    private BTNodeStatus ApproachTarget()
    {
        actionController.Move(DirectionToTarget());
        _agentUI.UpdateStatusText("Approach Target", 0.2f, 0);
        return BTNodeStatus.Success;
    }

}