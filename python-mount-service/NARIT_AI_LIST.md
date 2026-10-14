# NARIT AI observing lists

The Interesting Object page offers **Build List by AI**. Users specify 1–100 new objects, object types, and satellite catalogs. The server calculates the next/current night (Sun below −6 degrees) at the configured observatory site, shortlists targets above 10 degrees, and asks the configured model to choose catalog IDs. The Sun is excluded. Existing targets are retained, duplicates are ignored transactionally, and additions are stored in the configuration SQLite database with `ai_selected=true`.

## Connection

The adapter uses the NARIT OpenAI-compatible API supplied by the user:

- `NARIT_LLLM_BASE_URL`: defaults to `https://lllm.narit.or.th/v1`.
- `NARIT_LLLM_MODEL`: defaults to `Anthropic-NARIT-Local-AI-01`; the alternative is `OpenAI-NARIT-Local-AI-01`.
- `NARIT_LLLM_API_KEY`: required bearer credential, server environment only. Note the three L characters in LLLM.

It checks `/models` before calling `/chat/completions`, validates model IDs and JSON catalog selections, limits output tokens, and uses 60-second chat timeouts. HTTP 429/5xx and connection failures retry at most twice with exponential backoff. Invalid model JSON gets one schema correction attempt. Authentication/authorization errors are not retried. Errors shown to users contain no credential or Authorization header.

The current Windows host has no credential in its process, User or Machine environment. The authorized Linux credential path `/home/nfd/Ctd/.training/agent.env` does not exist on this host. An administrator must provision the credential through a secure channel; do not send it in chat or copy it from another machine. On an authorized nfd host, source the supplied file without displaying it, and unset secret shell variables after use. Keep the service credential in its protected server environment. The machine needs access to the NARIT network and 192.168.2.161.

No heuristic-only fallback is presented as AI. Live authenticated validation passed: `/models` includes the configured model, and an actual chat completion returned valid candidate-selection JSON. The Windows protected credential has been provisioned and the service restarted. Requests require the NARIT-MountController User-Agent.

## Execution and limits

Computation/network calls run in a separate thread, outside the tracking/telemetry command lane. Only one build can run at a time. Fixed catalog objects are sampled every 5 minutes; minor-body positions every 15 minutes; satellite passes every 60 seconds with a cylindrical Earth-shadow approximation. These are observing-list estimates, not motor tracking set points. Magnitudes are available for catalog targets; satellite/minor-body apparent brightness is not guaranteed. The model receives a bounded category-balanced shortlist; the full catalog is not sent. Invalid or duplicate model IDs reject the response without saving changes.

The backend routes are loaded in the running service. An earlier restart was rejected; after renewed user authorization the service restart succeeded.

## Verification

`python -m unittest discover -s tests -p test_interesting_ai.py`

`node tests/interesting-ai-ui.cjs`

The UI test mocks the AI response and does not command any telescope motion.

Build requests now stream real workflow-stage progress (preparation, visibility, model selection, validation, saving). Percentage remains at 80 while awaiting AI and does not imply token-level completion. The primary model was observed returning `finish_reason=length` with empty content at low output limits; requests now allow 4096 tokens and can retry using the supplied OpenAI-NARIT alternative when truncation occurs. The correction attempt is bounded at 8192 tokens when keeping the same model. A real 10-object response passed validation.
