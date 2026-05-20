using UnityEngine;

// Student template: replace BuildTree() with an attacker or defender BT strategy.
public class StudentBTStrategy : MonoBehaviour
{
    [SerializeField] private CombatCharacter self;
    [SerializeField] private CombatCharacter target;
    [SerializeField] private CombatActionController actionController;
    [SerializeField] private CooldownSystem cooldownSystem;

    [SerializeField] private float emergencyHealthRatio = 0.25f;
    [SerializeField] private float finishBlowHealthRatio = 0.2f;
    [SerializeField] private float attackDistance = 1.8f;
    [SerializeField] private float counterDodgeOffset = 1f;
    [SerializeField] private float chaseDistance = 2.5f;
    [SerializeField] private float flankTargetOffset = 0.5f;

    private bool _counterAttackQueued = false;
    private float _counterAttackDelayedTime;
    private float _countAttackExpireTime;
    

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
        // TODO: Choose an attacker or defender role.
        // TODO: Build a root SelectorNode or SequenceNode.
        // TODO: Add ConditionNode objects for health, distance, cooldown, and facing checks.
        // TODO: Add ActionNode objects that call only:
        // actionController.Move(direction), Attack(), Block(), or Dodge(direction).
        // TODO: Include at least two advanced elements in your final strategy:
        // DecoratorNode, ParallelNode, RandomSelectorNode, or another non-deterministic choice.
        root = new SelectorNode(
            new SequenceNode(
                new ConditionNode(ShouldEmergencyDodge),
                new ActionNode(DodgeAway)),
            new SequenceNode(
                new ConditionNode(ShouldFinishBlow),
                new ActionNode(Attack)),
            new SequenceNode(
                new ConditionNode(ShouldPerformQueuedCounterAttack),
                new ActionNode(CounterAttack)),
            new SequenceNode(
                new ConditionNode(ShouldDodgeCounter),
                new RandomSelectorNode(
                    new SequenceNode(
                        new ActionNode(LeftDodge),
                        new ActionNode(Attack)),
                    new SequenceNode(
                        new ActionNode(RightDodge),
                        new ActionNode(Attack)))),
            new SequenceNode(
                new DecoratorNode(
                    new SequenceNode(
                        new ConditionNode(ShouldApproachAttack),
                        new ActionNode(ApproachTarget)),
                    status =>
                    {
                        if (status == BTNodeStatus.Success)
                        {
                            return UnityEngine.Random.value < 0.9f
                                ? BTNodeStatus.Success
                                : BTNodeStatus.Failure;
                        }
                        return status;
                    }),
                new ActionNode(Attack)),
            new SequenceNode(
                new ConditionNode(ShouldBlockFeintApproach),
                new ActionNode(ApproachTarget)),
            new SequenceNode(
                new ConditionNode(ShouldChase),
                new ActionNode(ApproachTarget)),
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

    private bool ShouldEmergencyDodge()
    {
        return self.CurrentHealthRatio < emergencyHealthRatio
               && cooldownSystem != null 
               && cooldownSystem.IsDodgeReady();
    }

    private BTNodeStatus DodgeAway()
    {
        actionController.Face(DirectionToTarget());
        actionController.Dodge(-DirectionToTarget());
        return BTNodeStatus.Success;
    }

    private bool ShouldFinishBlow()
    {
        return target.CurrentHealthRatio < finishBlowHealthRatio
               && DistanceToTarget() < 2
               && cooldownSystem != null
               && cooldownSystem.IsAttackReady();
    }

    private BTNodeStatus Attack()
    {
        actionController.Face(DirectionToTarget());
        actionController.Attack();
        return BTNodeStatus.Success;
    }

    // Dodge가 거리가 가까워도 나갈 수 있으니, 거리 조건 추가 고려하기
    private bool ShouldDodgeCounter()
    {
        return target.ActionController.IsAttacking
            && cooldownSystem != null
            && cooldownSystem.IsDodgeReady();
    }
    
    private BTNodeStatus LeftDodge()
    {
        Vector3 leftOffsetPos = target.transform.position + 
                                Vector3.Cross(DirectionToTarget(), Vector3.up).normalized * counterDodgeOffset;
        Vector3 direction = (leftOffsetPos - self.transform.position).normalized;
        
        actionController.Face(direction.magnitude < 0.0001f ? self.transform.forward : direction);
        actionController.Dodge(direction.magnitude < 0.0001f ? self.transform.forward : direction);

        _counterAttackQueued = true;
        _counterAttackDelayedTime = Time.time + target.ActionController.DodgeInvincibleDuration;
        _countAttackExpireTime = _counterAttackDelayedTime + 0.5f;
        
        return BTNodeStatus.Success;
    }

    private BTNodeStatus RightDodge()
    {
        Vector3 rightOffsetPos = target.transform.position +
                                 Vector3.Cross(Vector3.up, DirectionToTarget()).normalized * counterDodgeOffset;
        Vector3 direction = (rightOffsetPos - self.transform.position).normalized;

        actionController.Face(direction.magnitude < 0.0001f ? self.transform.forward : direction);
        actionController.Dodge(direction.magnitude < 0.0001f ? self.transform.forward : direction);

        _counterAttackQueued = true;
        _counterAttackDelayedTime = Time.time + target.ActionController.DodgeInvincibleDuration;
        _countAttackExpireTime = _counterAttackDelayedTime + 0.5f;

        return BTNodeStatus.Success;
    }

    private bool ShouldPerformQueuedCounterAttack()
    {
        if (!_counterAttackQueued) return false;

        if (Time.time > _countAttackExpireTime)
        {
            _counterAttackQueued = false;
            return false;
        }

        return Time.time >= _counterAttackDelayedTime
               && DistanceToTarget() < attackDistance
               && cooldownSystem != null
               && cooldownSystem.IsAttackReady();
    }

    private BTNodeStatus CounterAttack()
    {
        _counterAttackQueued = false;
        return Attack();
    }

    private bool ShouldApproachAttack()
    {
        return !target.CooldownSystem.IsBlockReady()
               && DistanceToTarget() < attackDistance
               && cooldownSystem != null
               && cooldownSystem.IsAttackReady();
    }

    private BTNodeStatus ApproachTarget()
    {
        actionController.Move(DirectionToTarget());
        return BTNodeStatus.Success;
    }

    private bool ShouldBlockFeintApproach()
    {
        return target.CooldownSystem.IsBlockReady()
               && DistanceToTarget() >= attackDistance
               && DistanceToTarget() < chaseDistance;
    }

    private bool ShouldApplyFlankPressure()
    {
        return DistanceToTarget() >= attackDistance
               && DistanceToTarget() < chaseDistance + 0.5f;
    }

    private BTNodeStatus LeftFlank()
    {
        Vector3 leftOffsetPos = target.transform.position +
                                Vector3.Cross(DirectionToTarget(), Vector3.up).normalized * flankTargetOffset;
        Vector3 direction = (leftOffsetPos - self.transform.position).normalized;
        actionController.Move(direction.magnitude < 0.0001f ? DirectionToTarget() : direction);
        return BTNodeStatus.Success;
    }

    private BTNodeStatus RightFlank()
    {
        Vector3 rightOffsetPos = target.transform.position +
                                Vector3.Cross(Vector3.up, DirectionToTarget()).normalized * flankTargetOffset;
        Vector3 direction = (rightOffsetPos - self.transform.position).normalized;
        actionController.Move(direction.magnitude < 0.0001f ? DirectionToTarget() : direction);
        return BTNodeStatus.Success;
    }

    private bool ShouldChase()
    {
        return DistanceToTarget() >= chaseDistance;
    }
}
