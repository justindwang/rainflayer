"""LLM Backend abstraction using LiteLLM.

Single async `complete()` interface that routes to any provider.
Replaces the direct httpx→Novita calls in:
  - rainflayer/brain/model.py             (RoR2BrainModel — strategic decisions)
  - rainflayer/brain/counterboss_model.py (CounterbossModel — counter-builds)

ror2_soul_layer.py is NOT affected — it uses Kirito's ChatEngine (Claude API)
for commentary, and just reads RoR2BrainOutput values. No LLM calls there.

---- Switching providers ----
Set env vars in .env or shell before starting `python -m brain.main`:

  # Keep current default (Novita + Llama — no change needed)
  BRAIN_MODEL=openai/meta-llama/llama-4-maverick-17b-128e-instruct-fp8
  BRAIN_API_KEY=<novita key>           # or just leave NOVITA_API_KEY set

  # Switch brain to OpenAI GPT-4o-mini
  BRAIN_MODEL=openai/gpt-4o-mini
  BRAIN_API_KEY=sk-...

  # Switch brain to Anthropic
  BRAIN_MODEL=anthropic/claude-haiku-4-5-20251001
  BRAIN_API_KEY=<anthropic key>

  # Override counterboss independently (optional — falls back to BRAIN_* if unset)
  COUNTERBOSS_MODEL=openai/gpt-4o-mini
  COUNTERBOSS_API_KEY=sk-...

---- Wiring (orchestrator.py) ----
    brain_backend       = LLMBackend.from_env("BRAIN", novita_key=api_key)
    counterboss_backend = LLMBackend.from_env("COUNTERBOSS", fallback=brain_backend)
    brain_model         = RoR2BrainModel(backend=brain_backend)
    counterboss_model   = CounterbossModel(backend=counterboss_backend)
"""
from __future__ import annotations

import logging
import os
from typing import Optional

import litellm

logger = logging.getLogger(__name__)

# Suppress litellm's per-call success logging
litellm.success_callback = []
litellm.set_verbose = False
# Silently drop params that a given model doesn't support (e.g. temperature on gpt-5)
litellm.drop_params = True

_NOVITA_BASE = "https://api.novita.ai/openai/v1"
_NOVITA_MODEL = "meta-llama/llama-4-maverick-17b-128e-instruct-fp8"


class LLMBackend:
    """
    Thin async wrapper around litellm.acompletion.

    Holds one (model, api_key, api_base) triple and exposes a single
    `complete(system, user, ...)` coroutine. Both RoR2BrainModel and
    CounterbossModel accept a backend instance instead of raw api_key+model.
    """

    def __init__(
        self,
        model: str,
        api_key: Optional[str] = None,
        api_base: Optional[str] = None,
    ):
        self.model = model
        self.api_key = api_key
        self.api_base = api_base
        logger.info(f"[LLMBackend] model={model} base={api_base or '(provider default)'}")

    # ------------------------------------------------------------------
    # Factory helpers
    # ------------------------------------------------------------------

    @classmethod
    def novita(cls, api_key: str, model: str = _NOVITA_MODEL) -> "LLMBackend":
        """Novita.ai — current default for both brain and counterboss."""
        return cls(
            model=f"openai/{model}",
            api_key=api_key,
            api_base=_NOVITA_BASE,
        )

    @classmethod
    def from_env(
        cls,
        prefix: str,
        novita_key: Optional[str] = None,
        fallback: Optional["LLMBackend"] = None,
    ) -> "LLMBackend":
        """
        Build a backend from {PREFIX}_MODEL / {PREFIX}_API_KEY / {PREFIX}_API_BASE.

        Resolution order:
          1. {PREFIX}_MODEL env var  →  {PREFIX}_API_KEY / {PREFIX}_API_BASE
          2. If prefix vars absent and fallback given  →  return fallback as-is
          3. Default to Novita/Llama using novita_key (or NOVITA_API_KEY env var)

        Args:
            prefix:     Env var prefix, e.g. "BRAIN" or "COUNTERBOSS"
            novita_key: The NOVITA_API_KEY value already loaded by main.py
            fallback:   Backend to reuse when no prefix vars are set (e.g. share brain backend)
        """
        model_env    = os.environ.get(f"{prefix}_MODEL")
        api_key_env  = os.environ.get(f"{prefix}_API_KEY")
        api_base_env = os.environ.get(f"{prefix}_API_BASE")

        # Nothing configured for this prefix → reuse fallback or build Novita default
        if not model_env and not api_key_env and not api_base_env:
            if fallback is not None:
                return fallback
            # Default: Novita + Llama
            key = novita_key or os.environ.get("NOVITA_API_KEY")
            return cls.novita(api_key=key)

        model    = model_env or f"openai/{_NOVITA_MODEL}"
        api_key  = api_key_env or novita_key or os.environ.get("NOVITA_API_KEY")
        api_base = api_base_env

        # Auto-set Novita base URL when using the Llama model without an explicit base
        if api_base is None and _NOVITA_MODEL in model:
            api_base = _NOVITA_BASE

        return cls(model=model, api_key=api_key, api_base=api_base)

    # ------------------------------------------------------------------
    # Core completion
    # ------------------------------------------------------------------

    async def complete(
        self,
        system: str,
        user: str,
        max_tokens: int = 1024,
        temperature: float = 0.7,
        json_mode: bool = False,
        reasoning_effort: Optional[str] = "minimal",
    ) -> str:
        """
        Call the LLM and return the raw text content string.

        json_mode=True sets response_format={"type":"json_object"} — forces the model
        to output only valid JSON with no preamble. Silently dropped for providers that
        don't support it (e.g. Novita/Llama) via litellm.drop_params.

        Raises on HTTP / network errors — callers handle fallback.
        """
        kwargs: dict = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user",   "content": user},
            ],
            "temperature": temperature,
        }
        if json_mode:
            kwargs["response_format"] = {"type": "json_object"}
        if reasoning_effort is not None:
            kwargs["reasoning_effort"] = reasoning_effort
        if self.api_key:
            kwargs["api_key"] = self.api_key
        if self.api_base:
            kwargs["api_base"] = self.api_base

        logger.debug(f"[LLMBackend] Calling {self.model} — system={len(system)} chars, user={len(user)} chars, json_mode={json_mode}")
        response = await litellm.acompletion(**kwargs)
        choice = response.choices[0]
        content = choice.message.content
        usage = getattr(response, "usage", None)
        logger.debug(f"[LLMBackend] finish_reason={choice.finish_reason!r}, content_len={len(content) if content else 0}, usage={usage}")
        if not content:
            refusal = getattr(choice.message, "refusal", None)
            logger.warning(f"[LLMBackend] Empty content — finish_reason={choice.finish_reason!r}, refusal={refusal!r}, choice={choice}")
        return content or ""
