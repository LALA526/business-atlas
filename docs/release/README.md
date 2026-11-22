# T1.0 release handoff

`scripts/release/handoff.py` creates the final release evidence bundle from the immutable T1.0 trace and actual evidence. It includes all 154 requirement rows, G0–G7, source and contract hashes, deployment access, Self Project knowledge, risks, and human approval records.

Generate the bundle after the source/build/deploy evidence is available:

```bash
python scripts/release/handoff.py \
  --evidence docs/release/t1.0-evidence.json \
  --output docs/release/t1.0-handoff
```

The command exits `1` while checkout is `not_ready`; the manifest and handoff are still written so unfinished work remains visible. It exits `0` only when every requirement is `accepted` or explicitly `deferred_approved`, all gates are accepted, and QA, BA, Leader and ProductOwner human approvals are present. Implemented or verified evidence is never relabelled as acceptance.

The deterministic `knowledge-archive.zip` contains the project baseline, controlled contracts, actual Self Project AKP ledgers and architecture decisions. Credentials, runtime configuration and local secret files are excluded. Private application credentials remain in `~/.config/atlas/product-login.json` and must not be added to evidence or archives.

The checked-in `t1.0-evidence.json` is the current factual input, not a declaration of final acceptance. Update requirement rows with concrete implementation, API/database/event, test, knowledge and deployment references as those facts become available. Public preview access is at **https://atlas.lalapilot.com**; use the privately supplied application account. The source repository and the host-created PR provide the corresponding code history, while the host pipeline performs the single build and Mac8 deployment.
