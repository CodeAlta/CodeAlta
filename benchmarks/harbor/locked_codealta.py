"""CodeAlta CLI (``altacli run``) under the locked protocol of SWE-bench Pro v2.

Modelled on ``v2/tooling/locked_codex.py`` and ``locked_claude_code.py``:
  * install(): uploads a self-contained linux-x64 ``altacli`` tarball (``build-altacli.ps1`` / ``build-altacli.sh``)
    and extracts it to /opt/codealta; ``altacli_url`` + ``altacli_sha256`` fetch a pinned tarball in the sandbox
    instead, with the upload as the fallback. The setup phase has network; the agent phase does not need it.
  * run(): one ``altacli run --format jsonl`` in a fresh, ephemeral profile (no user configuration, no plugin, no MCP
    server, no web tool, no models.dev refresh), inside an in-sandbox ``timeout`` (budget - 60 s) so the process
    always ends; ``altacli --timeout`` (budget - 90 s) ends it first, cleanly, with its result line.
  * The repository's git diff against the base commit is always written to /logs/agent/model.patch, for
    ``patch_replay:PatchReplayAgent``.
  * Tokens and cost come from the ``_codealta/result`` line; what it does not give stays unset, never zero.

Usage (see README.md)::

    PYTHONPATH=v2/tooling:<codealta>/benchmarks/harbor harbor run -p v2/tasks -e docker \
      -a locked_codealta:LockedCodeAlta -m anthropic/claude-opus-5-5 --ak reasoning_effort=xhigh \
      --allow-agent-host api.anthropic.com --job-name sbp-v2-codealta-opus
"""
from __future__ import annotations

import json
import shlex
import tempfile
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from harbor.agents.installed.base import AgentAuthenticationError, BaseInstalledAgent
from harbor.environments.base import BaseEnvironment
from harbor.models.agent.context import AgentContext

_HERE = Path(__file__).resolve().parent
_DEFAULT_TARBALL = _HERE / "dist" / "altacli-linux-x64.tar.gz"
_INSTALL_DIR = "/opt/codealta"
_ALTACLI = f"{_INSTALL_DIR}/altacli"
_RUN_DIR = "/tmp/codealta-run"
_PROFILE_DIR = "/tmp/codealta-profile"
_LOGS = "/logs/agent"
_JSONL = f"{_LOGS}/altacli.jsonl"
_STDERR = f"{_LOGS}/altacli.stderr"
_DEFAULT_BUDGET_SEC = 3000  # [agent] timeout_sec of every SWE-bench Pro v2 task: 50 minutes

# Harbor provider (the part of -m before "/") -> CodeAlta provider type, API key variables (first found wins) and the
# model host to pass to --allow-agent-host. Any other provider needs --ak provider_type=... --ak api_key_env=...
_PROVIDERS: dict[str, tuple[str, tuple[str, ...], str]] = {
    "anthropic": ("anthropic", ("ANTHROPIC_API_KEY",), "api.anthropic.com"),
    "openai": ("openai-responses", ("OPENAI_API_KEY",), "api.openai.com"),
    "google": ("google-genai", ("GEMINI_API_KEY", "GOOGLE_API_KEY"), "generativelanguage.googleapis.com"),
    "gemini": ("google-genai", ("GEMINI_API_KEY", "GOOGLE_API_KEY"), "generativelanguage.googleapis.com"),
}
_EFFORTS = ("none", "minimal", "low", "medium", "high", "xhigh", "max")

# The repository: /app, or /testbed for a few tasks.
_FIND_REPO = (
    "git -c safe.directory='*' -C /app rev-parse --show-toplevel 2>/dev/null "
    "|| git -c safe.directory='*' -C /testbed rev-parse --show-toplevel 2>/dev/null || echo /app"
)

# The diff of the agent's work against the commit the task started from, without what CodeAlta itself may leave in the
# project (.alta/: tasks, plans). Same text format as the locked Codex and Claude Code agents. CodeAlta's agents may
# commit, on a branch of their own: HEAD goes back to the base commit first (the work stays in the files), so the
# verifier, which restores the test files from HEAD, grades the same change in the sandbox as on a pristine image.
_CAPTURE = (
    f"mkdir -p {_LOGS}; "
    f"repo=$(cat {_RUN_DIR}/repo 2>/dev/null || {{ {_FIND_REPO}; }}); "
    f"base=$(cat {_RUN_DIR}/base 2>/dev/null); "
    'cd "$repo" || { : > /logs/agent/model.patch; echo 0; exit 0; }; '
    "rm -f .git/index.lock 2>/dev/null; "
    "if [ -n \"$base\" ] && [ \"$(git -c safe.directory='*' rev-parse HEAD 2>/dev/null)\" != \"$base\" ]; then "
    f"git -c safe.directory='*' log --oneline \"$base..HEAD\" > {_LOGS}/agent-commits.txt 2>/dev/null; "
    "git -c safe.directory='*' reset -q --soft \"$base\" 2>/dev/null; fi; "
    "git -c safe.directory='*' add -A -- . ':(exclude).alta' 2>/dev/null; "
    "git -c safe.directory='*' diff --cached ${base:-HEAD} -- . ':(exclude).alta' > /logs/agent/model.patch 2>/dev/null; "
    "git -c safe.directory='*' reset -q 2>/dev/null; "
    "wc -c < /logs/agent/model.patch; true"
)


def _flag(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in ("1", "true", "yes", "on")


def _toml_string(value: str) -> str:
    # -c takes a TOML value; a JSON string is a valid TOML basic string for these values.
    return json.dumps(value)


class LockedCodeAlta(BaseInstalledAgent):
    """``altacli run`` in the sandbox, with patch capture for fresh-sandbox re-grading.

    Agent kwargs (``--ak key=value``), all optional:
      altacli_tarball   host path of the linux-x64 tarball (default: dist/altacli-linux-x64.tar.gz beside this file)
      altacli_url       URL of a pinned tarball fetched in the sandbox (needs altacli_sha256); upload on failure
      altacli_sha256    sha256 of that tarball
      reasoning_effort  none, minimal, low, medium, high, xhigh or max (-m provider:model@effort)
      timeout_sec       the agent budget in seconds (default 3000, the [agent] timeout of the tasks)
      provider_type     the CodeAlta provider type, for a provider not in the table (openai-chat, mistral, xai...)
      api_key_env       the variable that holds the API key (host environment or --ae); passed to the sandbox
      api_url           the base URL of the provider (OpenAI-compatible endpoints, proxies)
      prompt_id         the CodeAlta agent prompt (default: default)
      disable_tools     the --disable-tools patterns (default: web)
      plugins           true to start the plugins and MCP servers (default: false)
      extra_args        more altacli run arguments, shell-quoted (for example "-c chat.some_setting=value")
    """

    def __init__(
        self,
        *args: Any,
        altacli_tarball: str | None = None,
        altacli_url: str | None = None,
        altacli_sha256: str | None = None,
        reasoning_effort: str | None = None,
        timeout_sec: int | str | None = None,
        provider_type: str | None = None,
        api_key_env: str | None = None,
        api_url: str | None = None,
        prompt_id: str | None = None,
        disable_tools: str | None = "web",
        plugins: bool | str = False,
        extra_args: str | None = None,
        **kwargs: Any,
    ):
        super().__init__(*args, **kwargs)
        self._tarball = Path(altacli_tarball).expanduser() if altacli_tarball else _DEFAULT_TARBALL
        self._url = altacli_url
        self._sha256 = altacli_sha256
        self._effort = str(reasoning_effort).strip().lower() if reasoning_effort else None
        if self._effort is not None and self._effort not in _EFFORTS:
            raise ValueError(f"reasoning_effort must be one of {', '.join(_EFFORTS)}; got {reasoning_effort!r}")
        self._budget_sec = int(timeout_sec) if timeout_sec is not None else _DEFAULT_BUDGET_SEC
        if self._budget_sec <= 120:
            raise ValueError(f"timeout_sec must be more than 120 seconds; got {self._budget_sec}")
        self._provider_type = provider_type
        self._api_key_env = api_key_env
        self._api_url = api_url
        self._prompt_id = prompt_id
        self._disable_tools = disable_tools
        self._plugins = _flag(plugins)
        self._extra_args = shlex.split(extra_args) if extra_args else []
        self._result: dict[str, Any] | None = None

    @staticmethod
    def name() -> str:
        return "codealta-locked"

    def get_version_command(self) -> str | None:
        return f"{_ALTACLI} --version"

    # ----- install -----

    async def install(self, environment: BaseEnvironment) -> None:
        fetched = False
        if self._url:
            if not self._sha256:
                raise ValueError("altacli_url needs altacli_sha256")
            url, sha = shlex.quote(self._url), self._sha256
            fetch = (
                "set -e; cd /tmp && rm -f altacli.tar.gz; "
                f"if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -m 600 -o altacli.tar.gz {url}; "
                f"elif command -v wget >/dev/null 2>&1; then wget -q -T 600 -t 3 -O altacli.tar.gz {url}; "
                f"elif command -v python3 >/dev/null 2>&1; then python3 -c 'import urllib.request,sys; urllib.request.urlretrieve(sys.argv[1], \"altacli.tar.gz\")' {url}; "
                "else echo NO_DOWNLOADER; exit 3; fi; "
                f"echo '{sha}  altacli.tar.gz' | sha256sum -c - && echo FETCHED"
            )
            r = await environment.exec(command=fetch, user="root", timeout_sec=660)
            fetched = r.return_code == 0 and "FETCHED" in (r.stdout or "")
            if not fetched:
                self.logger.warning(f"in-sandbox fetch failed (rc={r.return_code}): {(r.stderr or '')[:200]}; uploading")
        if not fetched:
            if not self._tarball.is_file():
                raise RuntimeError(
                    f"altacli tarball not found: {self._tarball}. Build it with benchmarks/harbor/build-altacli.ps1 "
                    "(or build-altacli.sh), or pass --ak altacli_tarball=<path>.")
            await environment.upload_file(self._tarball, "/tmp/altacli.tar.gz")

        # A tarball made on Windows has no executable bits: set them on the two native executables.
        extract = (
            f"set -e; rm -rf {_INSTALL_DIR} && mkdir -p {_INSTALL_DIR} "
            f"&& tar -xzf /tmp/altacli.tar.gz -C {_INSTALL_DIR} --no-same-owner && rm -f /tmp/altacli.tar.gz "
            f"&& chmod -R a+rX {_INSTALL_DIR} && chmod 755 {_ALTACLI} && {{ chmod 755 {_INSTALL_DIR}/createdump 2>/dev/null || true; }} "
            f"&& {_ALTACLI} --version"
        )
        r = await environment.exec(command=extract, user="root", timeout_sec=300)
        if r.return_code != 0:
            raise RuntimeError(f"altacli install failed (rc={r.return_code}): {(r.stderr or r.stdout or '')[-500:]}")
        self.logger.info(f"altacli installed: {(r.stdout or '').strip()}")
        await self._ensure_tls(environment)

    async def _ensure_tls(self, environment: BaseEnvironment) -> None:
        """.NET needs libssl and the CA certificates to reach the model; install them when an image lacks them."""
        check = (
            "for f in /usr/lib/*/libssl.so.* /lib/*/libssl.so.* /usr/lib/libssl.so.* /lib/libssl.so.* /usr/lib64/libssl.so.*; "
            'do [ -e "$f" ] && { echo SSL_OK; break; }; done; '
            "{ [ -s /etc/ssl/certs/ca-certificates.crt ] || [ -s /etc/pki/tls/certs/ca-bundle.crt ] || [ -s /etc/ssl/cert.pem ]; } "
            "&& echo CA_OK; true"
        )
        r = await environment.exec(command=check, user="root", timeout_sec=60)
        out = r.stdout or ""
        if "SSL_OK" in out and "CA_OK" in out:
            return
        self.logger.warning(f"installing TLS prerequisites (libssl: {'SSL_OK' in out}, CA: {'CA_OK' in out})")
        install = (
            "if command -v apt-get >/dev/null 2>&1; then "
            "export DEBIAN_FRONTEND=noninteractive; "
            "(apt-get update -qq && apt-get install -y -qq --no-install-recommends openssl ca-certificates) >/dev/null 2>&1 || { "
            "sed -i -e 's|deb.debian.org/debian-security|archive.debian.org/debian-security|g' -e 's|security.debian.org|archive.debian.org|g' "
            "-e 's|deb.debian.org|archive.debian.org|g' /etc/apt/sources.list 2>/dev/null; "
            "apt-get -o Acquire::Check-Valid-Until=false update -qq >/dev/null 2>&1; "
            "apt-get install -y -qq --no-install-recommends openssl ca-certificates >/dev/null 2>&1; }; "
            "elif command -v apk >/dev/null 2>&1; then apk add --no-cache libssl3 ca-certificates >/dev/null 2>&1; "
            "elif command -v dnf >/dev/null 2>&1; then dnf install -y openssl-libs ca-certificates >/dev/null 2>&1; "
            "elif command -v yum >/dev/null 2>&1; then yum install -y openssl-libs ca-certificates >/dev/null 2>&1; fi; "
            + check
        )
        r = await environment.exec(command=install, user="root", timeout_sec=900)
        out = r.stdout or ""
        if "SSL_OK" not in out or "CA_OK" not in out:
            raise RuntimeError(f"altacli needs libssl and CA certificates; could not install them: {out.strip()}")

    # ----- run -----

    def _model_settings(self) -> tuple[str, list[str], str, str]:
        """The altacli -m value, the -c settings, the key variable and its value."""
        if not self.model_name or "/" not in self.model_name:
            raise ValueError("codealta-locked needs -m <provider>/<model>, for example -m anthropic/claude-opus-5-5")
        provider, model = self.model_name.split("/", 1)
        known = _PROVIDERS.get(provider.lower())
        provider_type = self._provider_type or (known[0] if known else None)
        if provider_type is None:
            raise ValueError(f"unknown provider {provider!r}: pass --ak provider_type=<CodeAlta type> --ak api_key_env=<VAR>")
        key_names = (self._api_key_env,) if self._api_key_env else (known[1] if known else ())
        if not key_names:
            raise ValueError(f"provider {provider!r}: pass --ak api_key_env=<VAR>")
        key_name, key_value = key_names[0], None
        for name in key_names:
            value = self._get_env(name)
            if value:
                key_name, key_value = name, value
                break
        if not key_value:
            raise AgentAuthenticationError(f"no API key: set {' or '.join(key_names)} on the host or with --ae")

        # The Gemini API names its models models/<id>; CodeAlta offers them under that name.
        if provider_type == "google-genai" and not model.startswith("models/"):
            model = f"models/{model}"
        provider_id = provider.lower()
        model_ref = f"{provider_id}:{model}" + (f"@{self._effort}" if self._effort else "")
        settings = [
            f"providers.{provider_id}.type={_toml_string(provider_type)}",
            f"providers.{provider_id}.api_key_env={_toml_string(key_name)}",
            f"chat.default_provider={_toml_string(provider_id)}",
        ]
        if self._api_url:
            settings.append(f"providers.{provider_id}.api_url={_toml_string(self._api_url)}")
        return model_ref, settings, key_name, key_value

    def _command(self, repo: str, model_ref: str, settings: list[str]) -> str:
        args = [
            _ALTACLI, "run",
            "--ephemeral", "--profile", _PROFILE_DIR, "--ignore-user-config",
            "-C", repo, "-m", model_ref,
            "--offline", "--format", "jsonl",
            # altacli ends the run itself first, with an abort in the journal and its result line; timeout is the backstop.
            "--timeout", str(self._budget_sec - 90),
        ]
        if self._disable_tools:
            args += ["--disable-tools", self._disable_tools]
        if not self._plugins:
            args += ["--no-plugins", "--no-mcp"]
        if self._prompt_id:
            args += ["--prompt-id", self._prompt_id]
        for setting in settings:
            args += ["-c", setting]
        args += self._extra_args
        args.append("-")
        run = " ".join(shlex.quote(a) for a in args)
        return (
            f"mkdir -p {_LOGS} {_PROFILE_DIR}; cd {shlex.quote(repo)} && "
            f"timeout -k 30 {self._budget_sec - 60} {run} < {_RUN_DIR}/instruction.md > {_JSONL} 2> {_STDERR}; "
            'echo "ALTACLI_EXIT=$?"'
        )

    async def run(self, instruction: str, environment: BaseEnvironment, context: AgentContext) -> None:
        instruction = self.render_instruction(instruction)
        try:
            model_ref, settings, key_name, key_value = self._model_settings()

            # The repository and its base commit, before the agent changes anything.
            r = await environment.exec(
                command=(f"mkdir -p {_RUN_DIR} {_LOGS} && repo=$({_FIND_REPO}) && echo \"$repo\" > {_RUN_DIR}/repo && "
                         f"git -c safe.directory='*' -C \"$repo\" rev-parse HEAD > {_RUN_DIR}/base 2>/dev/null; "
                         f"chmod -R a+rwX {_RUN_DIR}; cat {_RUN_DIR}/repo"),
                user="root", timeout_sec=120)
            repo = (r.stdout or "").strip().splitlines()[-1] if (r.stdout or "").strip() else "/app"

            with tempfile.TemporaryDirectory(prefix="codealta-instruction-") as tmp:
                local = Path(tmp) / "instruction.md"
                local.write_text(instruction, encoding="utf-8")
                await environment.upload_file(local, f"{_RUN_DIR}/instruction.md")
            await environment.exec(command=f"chmod a+r {_RUN_DIR}/instruction.md", user="root", timeout_sec=60)

            command = self._command(repo, model_ref, settings)
            self.logger.info(f"altacli: {command}")
            env = {key_name: key_value, "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
            result = await environment.exec(command=command, env=env, timeout_sec=self._budget_sec)
            exit_code = self._exit_code(result.stdout)
            await self._read_result(environment, context, exit_code)
            # A run that ends by its budget or by a failure is still graded: its patch is captured below. A run that
            # never started (usage, provider not ready, session in use) or failed on the provider is reported to
            # Harbor, which classifies it (rate limit, authentication...) for its retry policy.
            if exit_code not in (0, 4, 124, 137):
                failure = (self._result or {}).get("failure") or {}
                tail = await environment.exec(command=f"tail -c 4000 {_STDERR} 2>/dev/null; true", timeout_sec=60)
                detail = SimpleNamespace(return_code=exit_code, stdout=json.dumps(failure) if failure else "",
                                         stderr=tail.stdout or "")
                raise self._classify_exec_error("altacli run", detail)
        finally:
            try:
                r = await environment.exec(command=_CAPTURE, user="root", timeout_sec=300)
                self.logger.info(f"model.patch bytes: {(r.stdout or '').strip()}")
            except Exception as e:  # never mask the trial outcome
                self.logger.warning(f"patch capture failed: {e}")

    @staticmethod
    def _exit_code(stdout: str | None) -> int | None:
        for line in reversed((stdout or "").splitlines()):
            if line.startswith("ALTACLI_EXIT="):
                try:
                    return int(line.split("=", 1)[1])
                except ValueError:
                    return None
        return None

    async def _read_result(self, environment: BaseEnvironment, context: AgentContext, exit_code: int | None) -> None:
        r = await environment.exec(command=f"head -n 1 {_JSONL} 2>/dev/null; echo; tail -n 1 {_JSONL} 2>/dev/null; true",
                                   timeout_sec=120)
        lines = [line for line in (r.stdout or "").splitlines() if line.strip()]
        header = self._parse(lines[0], "_codealta/session") if lines else None
        result = self._parse(lines[-1], "_codealta/result") if lines else None
        self._fill(context, header, result, exit_code)

    @staticmethod
    def _parse(line: str, method: str) -> dict[str, Any] | None:
        try:
            record = json.loads(line)
        except ValueError:
            return None
        if isinstance(record, dict) and record.get("method") == method and isinstance(record.get("params"), dict):
            return record["params"]
        return None

    def _fill(self, context: AgentContext, header: dict[str, Any] | None, result: dict[str, Any] | None,
              exit_code: int | None) -> None:
        self._result = result
        metadata: dict[str, Any] = dict(context.metadata or {})
        metadata["altacli_exit_code"] = exit_code
        if header:
            metadata["codealta_session"] = {k: header.get(k) for k in ("sessionId", "provider", "model", "reasoning", "agentPrompt", "storage")}
        if result is None:
            # No result line: the process was killed (in-sandbox timeout) or crashed. Usage and cost stay unknown.
            metadata["codealta_result"] = None
            context.metadata = metadata
            return
        metadata["codealta_result"] = {k: result.get(k) for k in ("status", "exitCode", "stopReason", "abortReason", "failure", "durationMs", "cost")}
        usage = result.get("usage")
        if isinstance(usage, dict):
            # CodeAlta's input tokens include the cached ones, as Harbor's n_input_tokens does.
            if isinstance(usage.get("inputTokens"), int):
                context.n_input_tokens = usage["inputTokens"]
            if isinstance(usage.get("cachedReadTokens"), int):
                context.n_cache_tokens = usage["cachedReadTokens"]
            if isinstance(usage.get("outputTokens"), int):
                context.n_output_tokens = usage["outputTokens"]
            metadata["codealta_usage"] = usage
        cost = result.get("cost")
        if isinstance(cost, dict) and isinstance(cost.get("amount"), (int, float)):
            currency = str(cost.get("currency") or "").upper()
            if currency in ("USD", ""):
                context.cost_usd = float(cost["amount"])
        context.metadata = metadata

    def populate_context_post_run(self, context: AgentContext) -> None:
        """Fallback when run() could not read the result in the sandbox: the JSONL synced to the host."""
        path = self.logs_dir / "altacli.jsonl"
        if not path.is_file():
            return
        lines = [line for line in path.read_text(encoding="utf-8", errors="replace").splitlines() if line.strip()]
        if not lines:
            return
        self._fill(context, self._parse(lines[0], "_codealta/session"), self._parse(lines[-1], "_codealta/result"), None)
