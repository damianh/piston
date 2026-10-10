# Real multi-agent experiments (not launched by this harness)

Scripted simultaneous commands cannot measure agent turns or cognitive benefit.
This protocol requires separately approved sessions, transcript consent, model
cost ceiling, concurrency/resource capacity and experiment repetition count.
Do not create paid agents or sessions merely to validate the command runner.

Use fresh separate workspaces and sessions, identical task/source/test scope,
matched model/provider/reasoning/tool permissions, and counterbalanced equivalent
task variants to limit learning effects. Compare clean-baseline edits and edits
with a distinct deterministic known pre-existing failure. Capture the baseline
before each task so new regressions are distinguishable. Allow direct-dotnet
agents to discover baselines normally. Only expose history/scheduling capabilities
in a backend condition when they actually exist. A manually supplied baseline is
a separately labeled information-assistance condition.

Repeatable prompt template (instantiate equally for each condition):

> Implement the specified order/pricing [or persistence] behavior change in the
> supplied workspace. Preserve unrelated behavior. Establish and report the full
> test scope and final result, distinguishing any pre-existing failures. Do not
> fix unrelated failures. Use the assigned test interface. Stop when the requested
> change is correct and the result is attributable to the final edited source.

Record the actual prompt and hash, requested behavior, baseline revision,
expected final patch/oracle, model settings, assigned tools and session IDs.
Evaluate task correctness independently with a full test/diagnostics oracle on
the final source. Retain unsuccessful and timed-out tasks, not only successful
completions. Backend and dotnet must ultimately verify the same test scope.

## Sanitized transcript-import contract

Future imports are versioned JSON objects; no credentials or full private
transcripts are committed. The operator retains consented raw evidence separately.

```json
{
  "schema_version": 1,
  "source": "agent",
  "experiment_id": "approved-experiment",
  "trial_id": "task-variant-repetition",
  "batch_id": "concurrent-batch",
  "session_id": "consented-session-id",
  "condition": "dotnet",
  "model_settings": {"provider": "record-actual", "model": "record-actual", "reasoning": "record-actual"},
  "prompt_hash": "sha256-of-instantiated-prompt",
  "transcript_reference": "operator-retained-sanitized-evidence",
  "correct": true,
  "total_turns": 8,
  "pre_existing_failure_turns": null,
  "blocked_wait_intervals_ms": [[100, 400], [300, 500]],
  "agent_blocked_wait_ms": 400,
  "annotations": []
}
```

Numbers above are **schema illustrations, not measured data**. A turn is one
assistant response including its tool calls. Count turns devoted to diagnosing,
rerunning, explaining or fixing a pre-existing failure using two independent
annotators. Each annotation records turn index, both classifications, reason,
evidence reference and adjudication. Retain disagreement; do not substitute total
turns for failure-related turns. If annotations are missing, the latter is null.

Blocked-wait intervals require transcript/tool evidence that tests prevented
productive progress. Exclude intervals where independent work continued. Compute
the union per session, not summed overlapping tool durations; `Evidence.WaitUnion`
implements this calculation. Report raw test-tool latency separately. No transcript
or timing evidence means null metrics with a reason.

Report matched task/batch sample sizes, absolute waiting/turn counts, paired
differences and uncertainty, task correctness and attrition. Never infer cognitive
benefit from faster scripted commands. The initial harness specifies this import
contract but does not execute sessions, annotate transcripts or calculate a
real-agent gate result.
