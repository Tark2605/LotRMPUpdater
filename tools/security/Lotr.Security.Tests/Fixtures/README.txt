Fake API responses used by the tests. They are shaped after the public API documentation
(VirusTotal API v3: https://docs.virustotal.com/reference/overview ; Hybrid Analysis / Falcon Sandbox API v2:
https://www.hybrid-analysis.com/docs/api/v2) and trimmed to the fields lotr-security reads.
They were NOT captured from the live services (no account/key exists yet). When the first real key exists,
replace them with real recorded responses (see docs/review-after/G-security.md, "ha-api" / "vt-fixtures").
Placeholders: {SHA256} and {ANALYSIS_ID} are substituted by the tests.
