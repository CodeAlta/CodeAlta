---
status: done
---

# Graduated autonomy (fleet 4.0, M4)

Spec: `doc/fleet/orchestration.md` ("Autonomy and approvals", "Try before enabling"), `doc/fleet/security-and-privacy.md`
rule 9, roadmap M4. Personal scope now; teams and on-call resolvers plug into the approval router later.

## Design

- An automation opts in with `autonomy = { allowed_tools = [...], max_calls_per_hour = N, approval_timeout = M,
  on_timeout = "stop" | "wait", approvers = "owner" }` (minutes). Without it, an automation runs as today.
- **Enforced on the machine, never by the prompt**: a tool guard of the host (the session's `AgentToolInterceptor`,
  which sees every tool call: built-in, plugin, MCP, Code Mode calls, the CodeAlta tools an agent CLI calls) is
  registered for the run's session before its prompt is sent:
  - the host's own tools (`read_file`, `grep`, `shell_command`, `write_file`..., `alta`, `exec`) keep their guards:
    commands and edits follow the permission mode, which must ask (a run in an approve-everything mode is refused);
  - any other tool (MCP, plugin) runs without a person only when listed in `allowed_tools` and under the circuit
    breaker (`max_calls_per_hour`, per automation); otherwise the call is held as an approval;
  - an approval is a permission request of the session (the same review card, remote `answerPermission`, the
    phone's permissions), bound to that exact call (tool, arguments digest), never answerable by an agent.
- **Routing**: the run is `waiting` while an approval waits; the node reports it, and the server notifies who holds
  `automation.approve` on it by access control (its owner, the team that owns it, grants); `ApprovalRecipients` is the
  seam where on-call resolvers come too. The desktop shows
  waiting sessions as it does today.
- **Timeout**: after `approval_timeout` minutes, `stop` cancels the waiting requests and stops the run; `wait` keeps it.
- **Take over**: from the run, the person opens its session and takes it over: the guard stops timing it out and
  every further held call waits for that person; a node takes it through `automations.takeOver` (node API, additive).
- **Dry run** (Try): allowed tools are replaced by fakes that record the call (tool, arguments) on the run and answer
  the agent that it was recorded and not made; the reply is kept, as before.

## Slices

- [x] 1. Model: `autonomy` in the configuration (read, write, validate, fingerprint), RPC items, page types; tests.
- [x] 2. Host guard: tool guard registry and interceptor composed with the plugins' hooks; held calls as reviews,
  circuit breaker, dry-run fakes; tests.
- [x] 3. Engine and runner: guard per run, strict mode check, `waiting` status, timeout watchdog (stop/wait),
  recorded calls, dry run in Try; tests.
- [x] 4. Fleet: `waiting` reported, server notification to the router's recipients (owner), `automations.takeOver`
  on the node API; tests.
- [x] 5. Desktop: editor Autonomy section, run detail (waiting, Take over, recorded calls), RPC; frontend tests.
- [x] 6. Docs (`site/docs`), `codealta-fleet` skill, `alta automation show`; verification.
