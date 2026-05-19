using UnityEngine;

public enum StudentCombatRole
{
    Attacker,
    Defender
}

// Student template: replace BuildTree() with an attacker or defender BT strategy.
public class StudentBTStrategy : MonoBehaviour
{
    [SerializeField] private StudentCombatRole role = StudentCombatRole.Attacker;
    [SerializeField] private CombatCharacter self;
    [SerializeField] private CombatCharacter target;
    [SerializeField] private CombatActionController actionController;
    [SerializeField] private CooldownSystem cooldownSystem;
    [SerializeField] private float attackDistance = 1.8f;
    [SerializeField] private float closeDistance = 2.2f;
    [SerializeField] private float preferredDistance = 3.2f;
    [SerializeField] private float lowHealthRatio = 0.35f;
    [SerializeField] private float facingAngle = 45f;

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
        if (!CanTick())
        {
            return;
        }

        root.Tick();
    }

    private void BuildTree()
    {
        root = role == StudentCombatRole.Attacker
            ? BuildAttackerTree()
            : BuildDefenderTree();
    }

    private BTNode BuildAttackerTree()
    {
        return new SelectorNode(
            new SequenceNode(
                new ConditionNode(ShouldEmergencyDodge),
                BuildRandomDodge()),
            new SequenceNode(
                new ConditionNode(CanAttack),
                new ParallelNode(
                    2,
                    1,
                    new ConditionNode(() => IsFacingTarget(facingAngle)),
                    new ActionNode(Attack))),
            new SequenceNode(
                new ConditionNode(ShouldTurnTowardTarget),
                new DecoratorNode(
                    new ActionNode(MoveTowardTarget),
                    status => status == BTNodeStatus.Failure ? BTNodeStatus.Success : status)),
            new SequenceNode(
                new ConditionNode(ShouldPressureTarget),
                new DecoratorNode(
                    new ActionNode(MoveTowardTarget),
                    status => status == BTNodeStatus.Failure ? BTNodeStatus.Success : status)),
            new ActionNode(MoveTowardTarget));
    }

    private BTNode BuildDefenderTree()
    {
        return new SelectorNode(
            new SequenceNode(
                new ConditionNode(ShouldEmergencyDodge),
                BuildRandomDodge()),
            new SequenceNode(
                new ConditionNode(CanBlockIncomingAttack),
                new ParallelNode(
                    2,
                    1,
                    new ActionNode(MoveTowardTarget),
                    new ActionNode(Block))),
            new SequenceNode(
                new ConditionNode(CanDodgeCloseTarget),
                BuildRandomDodge()),
            new SequenceNode(
                new ConditionNode(CanCounterAttack),
                new ParallelNode(
                    2,
                    1,
                    new ActionNode(MoveTowardTarget),
                    new ActionNode(Attack))),
            new DecoratorNode(
                new ActionNode(MaintainDistance),
                status => status == BTNodeStatus.Failure ? BTNodeStatus.Success : status));
    }

    private BTNode BuildRandomDodge()
    {
        return new RandomSelectorNode(
            new ActionNode(DodgeAway),
            new ActionNode(DodgeLeft),
            new ActionNode(DodgeRight));
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

    private bool ShouldEmergencyDodge()
    {
        return self.CurrentHealthRatio <= lowHealthRatio
            && cooldownSystem != null
            && cooldownSystem.IsDodgeReady();
    }

    private bool CanAttack()
    {
        return cooldownSystem != null
            && cooldownSystem.IsAttackReady()
            && DistanceToTarget() <= attackDistance
            && IsFacingTarget(facingAngle);
    }

    private bool ShouldPressureTarget()
    {
        return target.CurrentHealthRatio <= 0.45f
            && DistanceToTarget() > attackDistance;
    }

    private bool ShouldTurnTowardTarget()
    {
        return DistanceToTarget() <= attackDistance
            && !IsFacingTarget(facingAngle);
    }

    private bool CanBlockIncomingAttack()
    {
        return cooldownSystem != null
            && cooldownSystem.IsBlockReady()
            && DistanceToTarget() <= closeDistance
            && (IsTargetAttacking() || IsTargetAttackReady());
    }

    private bool CanDodgeCloseTarget()
    {
        return cooldownSystem != null
            && cooldownSystem.IsDodgeReady()
            && DistanceToTarget() <= closeDistance
            && !CanBlockIncomingAttack();
    }

    private bool CanCounterAttack()
    {
        return cooldownSystem != null
            && cooldownSystem.IsAttackReady()
            && DistanceToTarget() <= attackDistance
            && IsFacingTarget(facingAngle)
            && !IsTargetAttacking();
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

    private BTNodeStatus MoveTowardTarget()
    {
        actionController.Move(DirectionToTarget());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus MaintainDistance()
    {
        float distance = DistanceToTarget();

        if (distance < preferredDistance)
        {
            actionController.Move(-DirectionToTarget());
        }
        else if (distance > preferredDistance + 0.75f)
        {
            actionController.Move(DirectionToTarget());
        }

        return BTNodeStatus.Success;
    }

    private BTNodeStatus Attack()
    {
        actionController.Attack();
        return BTNodeStatus.Success;
    }

    private BTNodeStatus Block()
    {
        actionController.Block();
        return BTNodeStatus.Success;
    }

    private BTNodeStatus DodgeAway()
    {
        actionController.Dodge(-DirectionToTarget());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus DodgeLeft()
    {
        actionController.Dodge(LeftOfTargetDirection());
        return BTNodeStatus.Success;
    }

    private BTNodeStatus DodgeRight()
    {
        actionController.Dodge(-LeftOfTargetDirection());
        return BTNodeStatus.Success;
    }

    private Vector3 LeftOfTargetDirection()
    {
        return Quaternion.Euler(0f, -90f, 0f) * DirectionToTarget();
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

        if (target == null)
        {
            CombatCharacter[] characters = FindObjectsByType<CombatCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            foreach (CombatCharacter character in characters)
            {
                if (character != self)
                {
                    target = character;
                    break;
                }
            }
        }
    }
}
