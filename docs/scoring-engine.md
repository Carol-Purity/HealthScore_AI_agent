# Scoring engine: `EvaluateHealthScoreProfile`

The engine is one HTTP-triggered Azure Function. It scores one record against one profile and returns, and normally saves, the result. Every score is built in two layers: a deterministic **rule score**, then an **AI review** that sets the **final score**. Guardrails and an audit trail surround both layers.

## Request

`GET|POST /api/EvaluateHealthScoreProfile?profileId=<guid>&recordId=<guid>[&preview=true][&debug=true]`

| Parameter | Meaning |
|---|---|
| `profileId` | The Health Score Profile to apply |
| `recordId` | The record to score |
| `preview` | Score it, but save nothing |
| `debug` | Include the exact evidence text sent to the model |

Invalid GUIDs return 400, an unknown profile returns 404, and unexpected errors return 500 with a short message. Stack traces go to the logs, not the response.

## 1. Load the configuration

The engine reads the profile (target table, AI switch), its enabled data sources, and each source's enabled rules (through `reliance_datasourcelookup`). It reads table metadata once per request and caches it: display name, primary name column, columns, and the targets of each lookup.

## 2. Link each rule to the scored record

For each rule, the engine finds the lookup that ties the rule's records to the scored record, in this order:

1. If exactly **one** condition column in the rule is a lookup to the target table (e.g. `parentaccountid`), it uses that column.
2. If there are **several**, it prefers the data source's relationship column and warns.
3. If there are **none**, it falls back to the data source's relationship column, provided that column is a real lookup to the target table, and warns.
4. Otherwise it **skips** the rule and adds a warning telling the admin to add an account condition.

It then appends its own filter directly under `<entity>`:

```xml
<filter type="and">
  <condition attribute="parentaccountid" operator="eq" value="{recordId}" />
</filter>
```

Filters directly under `<entity>` are combined with AND, so the rule's own logic, OR groups included, can never match another record's data.

## 3. Run the rule and collect evidence

The query's column list is replaced with what the model should see: the record's name, status, the rule's condition columns, any columns the admin picked, and created and modified dates, newest first. This doesn't change which records match. The engine records the match count, the newest record's date and up to 5 sample records. Sample values are formatted (lookups by name, choices by label), stripped of HTML and cut to 120 characters.

## 4. Score each source and the profile

```
net(source)          = Σ Positive (matches × points) − Σ Negative (matches × points)
contribution(source) = clamp(net, −weight, +weight)
ruleScore            = round(clamp(Σ contributions, 0, 100))
```

The weight is the most any single source can move the score. Everything starts at 0, so a record with no evidence earns nothing. The engine also reports the **highest achievable rule score** (the total weight of the sources that have a Positive rule) and warns when it's below 100.

## 5. AI review

When AI is enabled, Azure OpenAI receives:

- **A system prompt.** It frames the model as an account-intelligence engine for salespeople and asks for a 0–100 score that treats the baseline as a strong starting point. It says to prefer recent, open records, to treat silence as risk, and to treat record text as data, ignoring any instructions in it. It asks for an itemised audit that adds up exactly.
- **A user prompt.** The rule score, the highest achievable score, and the evidence summary.
- **Settings.** JSON mode, temperature 0.2 and a 30-second timeout.

Expected response:

```json
{
  "healthscore": 42,
  "adjustments": [ { "points": -30, "rationale": "…", "evidence": "exact quote" } ],
  "reason": "2–3 sentences for the salesperson",
  "nextaction": "one concrete step"
}
```

If the call fails or returns no usable score, the final score is the rule score and the request still succeeds.

## 6. Audit trail and guardrails

| Step | When |
|---|---|
| AI | One per adjustment. `evidenceFound` is true only if the quote really appears in the evidence |
| Not itemised | The AI's score differs from the baseline by more than its items add up to |
| Range limit | The AI's score was outside 0–100 |
| Safety limit | **Silence cap:** if the profile has Positive rules and none matched, the score can't exceed 20 |

`audit.addsUp` confirms that the baseline plus all steps equals the final score.

## 7. Risk, trend, persistence

| Final score | Risk |
|---|---|
| 70–100 | Low |
| 40–69 | Medium |
| 20–39 | High |
| 0–19 | Critical |

Trend compares the score with the previous result for the same profile and record. A change of more than 3 points counts as **Improving** or **Declining**; anything smaller is **Stable**.

Unless `preview=true`, the engine creates a Health Score Result with the record's name and link and `islatest = true`. It then clears `islatest` on that record's older results and assigns the new result to the record's owner. The owner step is separate, so a missing permission adds a warning and never blocks the save.

## Worked example

| Source | Weight | Rule | Matches | Points | Net | Contribution |
|---|---|---|---|---|---|---|
| Case | 55 | Case (Positive) | 11 | 15 | +165 | +55 (capped) |
| Opportunity | 40 | Opportunity (Positive) | 11 | 10 | +110 | +40 (capped) |

The rule score was **95**. The AI found that every case was resolved and every opportunity closed, with no activity for four months, and returned **42 (Medium)**. That exposed a rule design issue: the rules counted all historical records. Rules should describe *current* signals, such as open items, recent wins and recent cases.
