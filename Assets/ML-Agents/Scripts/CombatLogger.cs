using System.Collections.Generic;
using System.Text;
using System.IO;
using UnityEngine;

// BT vs RL 비교용 CSV 로거.
// 기존 코드를 수정하지 않고, 상태 변화를 폴링하여 행동을 카운트한다.
// EpisodeManager가 에피소드를 리셋할 때마다 한 줄씩 CSV에 기록한다.
public class CombatLogger : MonoBehaviour
{
    [Header("참조 (비우면 자동 탐색)")]
    [SerializeField] private CombatCharacter agentA;   // 우리 에이전트 (BT 또는 RL)
    [SerializeField] private CombatCharacter agentB;   // 상대 (baseline)
    [SerializeField] private EpisodeManager episodeManager;

    [Header("설정")]
    [Tooltip("BT_vs_baseline / RL_vs_baseline 등 대전 조건 이름")]
    [SerializeField] private string matchType = "RL_vs_baseline";

    [Tooltip("우리 에이전트(AgentA)가 BT면 true, RL이면 false")]
    [SerializeField] private bool agentAIsBT = false;

    [Tooltip("기록할 에피소드 수. 도달하면 자동으로 일시정지")]
    [SerializeField] private int targetEpisodes = 50;

    [Tooltip("CSV 저장 파일 이름 (프로젝트 루트에 생성)")]
    [SerializeField] private string fileName = "combat_log.csv";

    // 행동 카운트
    private int aAttack, aBlock, aDodge, aHit, aMiss;
    private int bAttack, bBlock, bDodge, bHit, bMiss;

    // 직전 상태 (전환 감지용)
    private bool aWasAttacking, aWasBlocking, aWasInvincible;
    private bool bWasAttacking, bWasBlocking, bWasInvincible;
    private float aPrevTargetHp;   // 상대(B) HP — A의 hit 감지용
    private float bPrevTargetHp;   // 상대(A) HP — B의 hit 감지용
    private bool aAttackLandedThisSwing;
    private bool bAttackLandedThisSwing;

    private float episodeStartTime;
    private int episodeId;
    private bool finished;
    private bool wasEpisodeDone;

    private string filePath;
    private StringBuilder csv = new StringBuilder();

    private void Start()
    {
        FillRefs();

        filePath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, fileName);

        // 헤더
        csv.AppendLine("episode_id,match_type,winner,survival_time,end_reason," +
                       "bt_final_hp,rl_final_hp," +
                       "bt_attack_count,bt_block_count,bt_dodge_count,bt_hit_count,bt_miss_count," +
                       "rl_attack_count,rl_block_count,rl_dodge_count,rl_hit_count,rl_miss_count");

        ResetCounters();
        episodeStartTime = Time.time;
        episodeId = 0;
        wasEpisodeDone = false;
    }

    private void Update()
    {
        if (finished) return;
        if (agentA == null || agentB == null || episodeManager == null) return;

        TrackActions();

        // 에피소드 종료 감지 (false→true 전환 시 1회 기록)
        bool done = episodeManager.IsEpisodeDone();
        if (done && !wasEpisodeDone)
        {
            RecordEpisode();
            episodeId++;

            if (episodeId >= targetEpisodes)
            {
                WriteFile();
                finished = true;
                Debug.Log($"[CombatLogger] {targetEpisodes} 에피소드 기록 완료 → {filePath}");
                Debug.Break(); // 에디터 일시정지
            }
        }

        // 리셋 감지 (true→false): 다음 에피소드 시작
        if (!done && wasEpisodeDone)
        {
            ResetCounters();
            episodeStartTime = Time.time;
        }

        wasEpisodeDone = done;
    }

    private void TrackActions()
    {
        CombatActionController acA = agentA.ActionController;
        CombatActionController acB = agentB.ActionController;
        if (acA == null || acB == null) return;

        // ── AgentA 행동 시작 감지 ──
        if (acA.IsAttacking && !aWasAttacking) { aAttack++; aAttackLandedThisSwing = false; }
        if (acA.IsBlocking && !aWasBlocking) aBlock++;
        if (acA.IsInvincible && !aWasInvincible) aDodge++;

        // ── AgentB 행동 시작 감지 ──
        if (acB.IsAttacking && !bWasAttacking) { bAttack++; bAttackLandedThisSwing = false; }
        if (acB.IsBlocking && !bWasBlocking) bBlock++;
        if (acB.IsInvincible && !bWasInvincible) bDodge++;

        // ── hit 감지: 상대 HP가 줄면 직전 공격자가 적중 ──
        // A가 공격중일 때 B의 HP 감소 → A hit
        if (acA.IsAttacking && agentB.CurrentHealth < aPrevTargetHp && !aAttackLandedThisSwing)
        {
            aHit++;
            aAttackLandedThisSwing = true;
        }
        // B가 공격중일 때 A의 HP 감소 → B hit
        if (acB.IsAttacking && agentA.CurrentHealth < bPrevTargetHp && !bAttackLandedThisSwing)
        {
            bHit++;
            bAttackLandedThisSwing = true;
        }

        // ── miss 감지: 공격이 끝났는데(IsAttacking true→false) 적중 못함 ──
        if (!acA.IsAttacking && aWasAttacking && !aAttackLandedThisSwing) aMiss++;
        if (!acB.IsAttacking && bWasAttacking && !bAttackLandedThisSwing) bMiss++;

        // 상태 갱신
        aWasAttacking = acA.IsAttacking;
        aWasBlocking = acA.IsBlocking;
        aWasInvincible = acA.IsInvincible;
        bWasAttacking = acB.IsAttacking;
        bWasBlocking = acB.IsBlocking;
        bWasInvincible = acB.IsInvincible;
        aPrevTargetHp = agentB.CurrentHealth;
        bPrevTargetHp = agentA.CurrentHealth;
    }

    private void RecordEpisode()
    {
        float survival = Time.time - episodeStartTime;

        bool aDead = agentA.IsDead;
        bool bDead = agentB.IsDead;

        // winner / end_reason 판정
        string winner;
        string endReason;
        string ourSide = agentAIsBT ? "BT" : "RL";   // AgentA가 우리 측

        if (aDead && bDead) { winner = "draw"; endReason = "both_dead"; }
        else if (bDead)     { winner = ourSide; endReason = "death"; }       // 상대 사망 → 우리 승
        else if (aDead)     { winner = "baseline"; endReason = "death"; }    // 우리 사망 → 패배
        else                { winner = "draw"; endReason = "timeout"; }

        // bt_* / rl_* 컬럼 매핑
        // AgentA = 우리(BT or RL), AgentB = baseline
        // 교수님 양식의 bt_/rl_ 컬럼에 "우리 측" 데이터를 채움
        int btAtk, btBlk, btDdg, btHit, btMiss;
        int rlAtk, rlBlk, rlDdg, rlHit, rlMiss;
        float btHp, rlHp;

        if (agentAIsBT)
        {
            // 우리가 BT → A 데이터를 bt_*에
            btAtk = aAttack; btBlk = aBlock; btDdg = aDodge; btHit = aHit; btMiss = aMiss;
            btHp = agentA.CurrentHealth;
            // baseline을 rl_* 자리에 (비교 기준)
            rlAtk = bAttack; rlBlk = bBlock; rlDdg = bDodge; rlHit = bHit; rlMiss = bMiss;
            rlHp = agentB.CurrentHealth;
        }
        else
        {
            // 우리가 RL → A 데이터를 rl_*에
            rlAtk = aAttack; rlBlk = aBlock; rlDdg = aDodge; rlHit = aHit; rlMiss = aMiss;
            rlHp = agentA.CurrentHealth;
            btAtk = bAttack; btBlk = bBlock; btDdg = bDodge; btHit = bHit; btMiss = bMiss;
            btHp = agentB.CurrentHealth;
        }

        csv.AppendLine($"{episodeId},{matchType},{winner},{survival:F2},{endReason}," +
                       $"{btHp:F0},{rlHp:F0}," +
                       $"{btAtk},{btBlk},{btDdg},{btHit},{btMiss}," +
                       $"{rlAtk},{rlBlk},{rlDdg},{rlHit},{rlMiss}");

        Debug.Log($"[CombatLogger] ep {episodeId} 기록: winner={winner}, time={survival:F1}s");
    }

    private void WriteFile()
    {
        File.WriteAllText(filePath, csv.ToString());
        Debug.Log($"[CombatLogger] CSV 저장 완료: {filePath}");
    }

    private void ResetCounters()
    {
        aAttack = aBlock = aDodge = aHit = aMiss = 0;
        bAttack = bBlock = bDodge = bHit = bMiss = 0;
        aWasAttacking = aWasBlocking = aWasInvincible = false;
        bWasAttacking = bWasBlocking = bWasInvincible = false;
        aAttackLandedThisSwing = bAttackLandedThisSwing = false;
        aPrevTargetHp = agentB != null ? agentB.CurrentHealth : 100f;
        bPrevTargetHp = agentA != null ? agentA.CurrentHealth : 100f;
    }

    private void FillRefs()
    {
        if (episodeManager == null) episodeManager = FindFirstObjectByType<EpisodeManager>();
        if (agentA == null)
        {
            GameObject go = GameObject.Find("Agent_A");
            if (go == null) go = GameObject.Find("AgentA");
            if (go != null) agentA = go.GetComponent<CombatCharacter>();
        }
        if (agentB == null)
        {
            GameObject go = GameObject.Find("Agent_B");
            if (go == null) go = GameObject.Find("AgentB");
            if (go != null) agentB = go.GetComponent<CombatCharacter>();
        }
    }
}
