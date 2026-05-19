using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

[RequireComponent(typeof(CombatCharacter))]
[RequireComponent(typeof(CooldownSystem))]
[RequireComponent(typeof(CombatActionController))]
public class StudentCombatAgent : Agent
{
    public StudentCombatRole role = StudentCombatRole.Attacker;
    public CombatCharacter self;
    public CombatCharacter opponent;
    public CombatActionController actionController;
    public CooldownSystem cooldownSystem;
    public EpisodeManager episodeManager;

    // Action constants for a clear RL action space.
    // Branch 0: movement (0 = stay, 1 = forward, 2 = backward, 3 = left, 4 = right)
    // Branch 1: skill (0 = none, 1 = attack, 2 = block, 3 = dodge)
    // Make sure the Behavior Parameters action space in Unity Editor matches these constants.
    private const int MoveStay = 0;
    private const int MoveForward = 1;
    private const int MoveBackward = 2;
    private const int MoveLeft = 3;
    private const int MoveRight = 4;
    private const int SkillNone = 0;
    private const int SkillAttack = 1;
    private const int SkillBlock = 2;
    private const int SkillDodge = 3;
    private const float MaxRelevantDistance = 10f;
    private const float AttackDistance = 1.8f;
    private const float DangerDistance = 2.2f;
    private const float DefenderPreferredDistance = 3.2f;
    private const float MoveHoldDuration = 0.25f;
    private const float CloseRange = 2.8f;

    private float previousSelfHealth;
    private float previousOpponentHealth;
    private float previousDistance;
    private Vector3 requestedMoveDirection;
    private float requestedMoveExpireTime;

    public override void Initialize()
    {
        FillDefaultReferences();
    }

    private void Reset()
    {
        FillDefaultReferences();
    }

    private void Update()
    {
        if (CanAct()
            && requestedMoveDirection.sqrMagnitude > 0.0001f
            && Time.time <= requestedMoveExpireTime)
        {
            actionController.Move(requestedMoveDirection);
        }
    }

    public override void OnEpisodeBegin()
    {
        FillDefaultReferences();
        requestedMoveDirection = Vector3.zero;

        if (episodeManager != null)
        {
            episodeManager.ResetEpisode();
        }

        previousSelfHealth = self != null ? self.CurrentHealth : 0f;
        previousOpponentHealth = opponent != null ? opponent.CurrentHealth : 0f;
        previousDistance = DistanceToOpponent();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Vector3 direction = DirectionToOpponent();
        float distance = DistanceToOpponent();
        CooldownSystem opponentCooldown = opponent != null ? opponent.CooldownSystem : null;
        CombatActionController opponentAction = opponent != null ? opponent.ActionController : null;

        sensor.AddObservation(self != null ? self.CurrentHealthRatio : 0f);
        sensor.AddObservation(opponent != null ? opponent.CurrentHealthRatio : 0f);
        sensor.AddObservation(Mathf.Clamp01(distance / MaxRelevantDistance));
        sensor.AddObservation(Vector3.Dot(transform.right, direction));
        sensor.AddObservation(Vector3.Dot(transform.forward, direction));
        sensor.AddObservation(role == StudentCombatRole.Attacker ? 1f : 0f);
        sensor.AddObservation(cooldownSystem != null ? cooldownSystem.GetAttackCooldownRatio() : 1f);
        sensor.AddObservation(cooldownSystem != null ? cooldownSystem.GetBlockCooldownRatio() : 1f);
        sensor.AddObservation(cooldownSystem != null ? cooldownSystem.GetDodgeCooldownRatio() : 1f);
        sensor.AddObservation(opponentCooldown != null ? opponentCooldown.GetAttackCooldownRatio() : 1f);
        sensor.AddObservation(opponentCooldown != null ? opponentCooldown.GetBlockCooldownRatio() : 1f);
        sensor.AddObservation(opponentCooldown != null ? opponentCooldown.GetDodgeCooldownRatio() : 1f);
        sensor.AddObservation(actionController != null && actionController.IsAttacking ? 1f : 0f);
        sensor.AddObservation(actionController != null && actionController.IsBlocking ? 1f : 0f);
        sensor.AddObservation(opponentAction != null && opponentAction.IsAttacking ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!CanAct())
        {
            AddReward(-0.01f);
            EndEpisode();
            return;
        }

        int moveAction = actions.DiscreteActions[0];
        int skillAction = actions.DiscreteActions[1];
        float distanceBeforeAction = DistanceToOpponent();
        bool attackReady = cooldownSystem != null && cooldownSystem.IsAttackReady();
        bool blockReady = cooldownSystem != null && cooldownSystem.IsBlockReady();
        bool dodgeReady = cooldownSystem != null && cooldownSystem.IsDodgeReady();

        UpdateRequestedMove(moveAction);
        ApplySkill(skillAction, moveAction);
        RewardActionChoice(skillAction, distanceBeforeAction, attackReady, blockReady, dodgeReady);
        RewardHealthDelta();
        RewardPositioning(moveAction);
        if (CheckEpisodeEnd())
        {
            return;
        }

        previousSelfHealth = self.CurrentHealth;
        previousOpponentHealth = opponent.CurrentHealth;
        previousDistance = DistanceToOpponent();
        AddReward(-0.001f);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<int> discreteActions = actionsOut.DiscreteActions;
        discreteActions[0] = MoveStay;
        discreteActions[1] = SkillNone;

        if (Input.GetKey(KeyCode.W))
        {
            discreteActions[0] = MoveForward;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            discreteActions[0] = MoveBackward;
        }
        else if (Input.GetKey(KeyCode.A))
        {
            discreteActions[0] = MoveLeft;
        }
        else if (Input.GetKey(KeyCode.D))
        {
            discreteActions[0] = MoveRight;
        }

        if (Input.GetKey(KeyCode.J))
        {
            discreteActions[1] = SkillAttack;
        }
        else if (Input.GetKey(KeyCode.K))
        {
            discreteActions[1] = SkillBlock;
        }
        else if (Input.GetKey(KeyCode.L))
        {
            discreteActions[1] = SkillDodge;
        }
    }

    private bool CanAct()
    {
        return self != null
            && opponent != null
            && actionController != null
            && !self.IsDead
            && !opponent.IsDead;
    }

    private void UpdateRequestedMove(int moveAction)
    {
        Vector3 moveDirection = GetMoveDirection(moveAction);

        if (moveDirection.sqrMagnitude > 0.0001f)
        {
            requestedMoveDirection = moveDirection;
            requestedMoveExpireTime = Time.time + MoveHoldDuration;
        }
    }

    private Vector3 GetMoveDirection(int moveAction)
    {
        Vector3 forward = DirectionToOpponent();
        Vector3 left = Quaternion.Euler(0f, -90f, 0f) * forward;
        Vector3 moveDirection = Vector3.zero;
        float distance = DistanceToOpponent();

        if (moveAction == MoveStay
            && role == StudentCombatRole.Attacker
            && distance > AttackDistance)
        {
            moveDirection = forward;
        }
        else if (moveAction == MoveForward)
        {
            moveDirection = forward;
        }
        else if (moveAction == MoveBackward)
        {
            moveDirection = -forward;
        }
        else if (moveAction == MoveLeft)
        {
            moveDirection = left;
        }
        else if (moveAction == MoveRight)
        {
            moveDirection = -left;
        }

        return moveDirection;
    }

    private void ApplySkill(int skillAction, int moveAction)
    {
        if (skillAction == SkillAttack)
        {
            actionController.Attack();
        }
        else if (skillAction == SkillBlock)
        {
            actionController.Block();
        }
        else if (skillAction == SkillDodge)
        {
            actionController.Dodge(DodgeDirection(moveAction));
        }
    }

    private Vector3 DodgeDirection(int moveAction)
    {
        if (moveAction == MoveForward)
        {
            return DirectionToOpponent();
        }

        if (moveAction == MoveLeft || moveAction == MoveRight)
        {
            Vector3 left = Quaternion.Euler(0f, -90f, 0f) * DirectionToOpponent();
            return moveAction == MoveLeft ? left : -left;
        }

        return -DirectionToOpponent();
    }

    private void RewardActionChoice(
        int skillAction,
        float distanceBeforeAction,
        bool attackReady,
        bool blockReady,
        bool dodgeReady)
    {
        if (skillAction == SkillAttack)
        {
            if (!attackReady)
            {
                AddReward(-0.02f);
            }
            else if (distanceBeforeAction <= AttackDistance && IsFacingOpponent(50f))
            {
                AddReward(role == StudentCombatRole.Attacker ? 0.04f : 0.02f);
            }
            else
            {
                AddReward(-0.01f);
            }
        }
        else if (skillAction == SkillBlock)
        {
            if (!blockReady)
            {
                AddReward(-0.02f);
            }
            else if (IsOpponentThreatening(distanceBeforeAction))
            {
                AddReward(role == StudentCombatRole.Defender ? 0.05f : 0.035f);
            }
        }
        else if (skillAction == SkillDodge)
        {
            if (!dodgeReady)
            {
                AddReward(-0.02f);
            }
            else if (IsOpponentThreatening(distanceBeforeAction) || self.CurrentHealthRatio < 0.35f)
            {
                AddReward(role == StudentCombatRole.Defender ? 0.05f : 0.04f);
            }
        }
    }

    private void RewardHealthDelta()
    {
        float damageDealt = Mathf.Max(0f, previousOpponentHealth - opponent.CurrentHealth);
        float damageTaken = Mathf.Max(0f, previousSelfHealth - self.CurrentHealth);
        float dealtRatio = opponent.MaxHealth <= 0f ? 0f : damageDealt / opponent.MaxHealth;
        float takenRatio = self.MaxHealth <= 0f ? 0f : damageTaken / self.MaxHealth;

        if (role == StudentCombatRole.Attacker)
        {
            AddReward(dealtRatio * 1.2f);
            AddReward(-takenRatio);
        }
        else
        {
            AddReward(dealtRatio * 0.7f);
            AddReward(-takenRatio * 1.2f);

            if (damageTaken <= 0f && DistanceToOpponent() <= DangerDistance)
            {
                AddReward(0.005f);
            }
        }
    }

    private void RewardPositioning(int moveAction)
    {
        float distance = DistanceToOpponent();
        float distanceDelta = previousDistance - distance;

        if (role == StudentCombatRole.Attacker)
        {
            if (distance > AttackDistance && moveAction == MoveStay)
            {
                AddReward(-0.015f);
            }

            if (distance > AttackDistance && distanceDelta > 0f)
            {
                AddReward(0.01f);
            }

            if (distance <= AttackDistance && IsFacingOpponent(50f))
            {
                AddReward(0.01f);
            }

            if (distance <= CloseRange && (moveAction == MoveLeft || moveAction == MoveRight))
            {
                AddReward(0.006f);
            }

            if (distance < AttackDistance * 0.65f && moveAction == MoveBackward)
            {
                AddReward(0.006f);
            }
        }
        else
        {
            bool inPreferredBand = distance >= DangerDistance && distance <= DefenderPreferredDistance + 1f;
            if (inPreferredBand)
            {
                AddReward(0.01f);
            }
            else if (distance < DangerDistance && moveAction == MoveBackward)
            {
                AddReward(0.01f);
            }
        }
    }

    private bool CheckEpisodeEnd()
    {
        if (self.IsDead && opponent.IsDead)
        {
            EndEpisode();
            return true;
        }

        if (opponent.IsDead)
        {
            AddReward(role == StudentCombatRole.Attacker ? 1.5f : 1.0f);
            EndEpisode();
            return true;
        }

        if (self.IsDead)
        {
            AddReward(-1.5f);
            EndEpisode();
            return true;
        }

        if (episodeManager != null && episodeManager.IsEpisodeDone())
        {
            float healthAdvantage = self.CurrentHealthRatio - opponent.CurrentHealthRatio;
            AddReward(role == StudentCombatRole.Defender ? 0.5f + healthAdvantage : healthAdvantage);
            EndEpisode();
            return true;
        }

        return false;
    }

    private bool IsOpponentThreatening(float distance)
    {
        CombatActionController opponentAction = opponent.ActionController;
        CooldownSystem opponentCooldown = opponent.CooldownSystem;

        return distance <= DangerDistance
            && ((opponentAction != null && opponentAction.IsAttacking)
                || (opponentCooldown != null && opponentCooldown.IsAttackReady()));
    }

    private bool IsFacingOpponent(float maxAngle)
    {
        Vector3 direction = DirectionToOpponent();
        Vector3 forward = transform.forward;
        forward.y = 0f;
        return Vector3.Angle(forward, direction) <= maxAngle;
    }

    private Vector3 DirectionToOpponent()
    {
        if (opponent == null)
        {
            return transform.forward;
        }

        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        return offset.sqrMagnitude <= 0.0001f ? transform.forward : offset.normalized;
    }

    private float DistanceToOpponent()
    {
        if (opponent == null)
        {
            return MaxRelevantDistance;
        }

        Vector3 offset = opponent.transform.position - transform.position;
        offset.y = 0f;
        return offset.magnitude;
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

        if (opponent == null)
        {
            CombatCharacter[] characters = FindObjectsByType<CombatCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            foreach (CombatCharacter character in characters)
            {
                if (character != self)
                {
                    opponent = character;
                    break;
                }
            }
        }
    }
}
