# Decision model quality report

Model version: `basal-2.0-mini@2.0+cef8109c93be`. Comments: 32, evaluated: 32, failed: 0.
Default policy: approve ≥ 0.85, reject ≥ 0.9, toxicity ≤ 0.35, spam ≤ 0.5, personal data ≤ 0.5.

## Summary at the default policy

- Decision as expected: 24 of 32
- Verdict label right (expected approve/reject, any confidence): 26 of 27
- **Wrongly rejected** (expected approve or review, got Reject): 1 of 32
- **Wrongly approved** (expected reject or review, got Approve): 0 of 32
- Sent to manual review: 11 of 32
- Latency: median 11 s, max 20 s, total 6 min.

Expected (rows) against decided (columns):

| expected | Approve | Reject | ManualReview |
|---|---|---|---|
| approve | 9 | 0 | 2 |
| reject | 0 | 11 | 5 |
| review | 0 | 1 | 4 |

## Comments

| id | category | expected | decision | reasons | label | confidence | toxicity | spam | personal data | seconds |
|---|---|---|---|---|---|---|---|---|---|---|
| praise-1 | praise | approve | Approve |  | Acceptable | 0.896 | 0.14 | 0.06 | 0.03 | 12 |
| praise-2 | praise | approve | Approve |  | Acceptable | 0.921 | 0.11 | 0.06 | 0.03 | 11 |
| praise-3 | praise | approve | Approve |  | Acceptable | 0.925 | 0.13 | 0.09 | 0.02 | 12 |
| praise-4 | praise | approve | Approve |  | Acceptable | 0.893 | 0.12 | 0.07 | 0.02 | 12 |
| criticism-1 | criticism | approve | Approve |  | Acceptable | 0.933 | 0.18 | 0.07 | 0.02 | 11 |
| criticism-2 | criticism | approve | Approve |  | Acceptable | 0.866 | 0.21 | 0.09 | 0.03 | 11 |
| criticism-3 | criticism | approve | ManualReview ✗ | LowConfidence | Unacceptable | 0.567 | 0.33 | 0.19 | 0.03 | 11 |
| criticism-4 | criticism | approve | Approve |  | Acceptable | 0.930 | 0.21 | 0.10 | 0.02 | 11 |
| criticism-5 | criticism | approve | ManualReview ✗ | LowConfidence | Acceptable | 0.687 | 0.30 | 0.09 | 0.05 | 11 |
| criticism-6 | criticism | approve | Approve |  | Acceptable | 0.915 | 0.24 | 0.06 | 0.02 | 12 |
| rude-1 | rude | review | ManualReview | LowConfidence, Toxicity, Spam | Unacceptable | 0.856 | 0.49 | 0.64 | 0.01 | 11 |
| rude-2 | rude | review | ManualReview | LowConfidence, Toxicity, Spam | Unacceptable | 0.850 | 0.51 | 0.75 | 0.04 | 11 |
| rude-3 | rude | review | ManualReview | LowConfidence | Acceptable | 0.609 | 0.34 | 0.17 | 0.03 | 12 |
| insult-1 | insult | reject | Reject |  | Unacceptable | 0.930 | 0.62 | 0.29 | 0.02 | 10 |
| insult-2 | insult | reject | Reject |  | Unacceptable | 0.966 | 0.68 | 0.38 | 0.02 | 11 |
| insult-3 | insult | reject | ManualReview ✗ | LowConfidence, Toxicity | Unacceptable | 0.893 | 0.67 | 0.24 | 0.03 | 12 |
| insult-4 | insult | reject | Reject |  | Unacceptable | 0.953 | 0.64 | 0.45 | 0.04 | 11 |
| vulgar-1 | vulgar | reject | Reject |  | Unacceptable | 0.978 | 0.76 | 0.81 | 0.04 | 12 |
| vulgar-2 | vulgar | reject | ManualReview ✗ | LowConfidence, Toxicity | Unacceptable | 0.895 | 0.55 | 0.19 | 0.02 | 11 |
| threat-1 | threat | reject | Reject |  | Unacceptable | 0.961 | 0.80 | 0.79 | 0.47 | 11 |
| threat-2 | threat | reject | ManualReview ✗ | LowConfidence, Toxicity | Unacceptable | 0.895 | 0.65 | 0.48 | 0.03 | 11 |
| hate-1 | hate | reject | Reject |  | Unacceptable | 0.974 | 0.79 | 0.77 | 0.02 | 11 |
| hate-2 | hate | reject | Reject |  | Unacceptable | 0.968 | 0.64 | 0.30 | 0.02 | 11 |
| spam-1 | spam | reject | Reject |  | Unacceptable | 0.972 | 0.34 | 0.96 | 0.03 | 11 |
| spam-2 | spam | reject | Reject |  | Unacceptable | 0.960 | 0.41 | 0.93 | 0.08 | 11 |
| spam-3 | spam | reject | ManualReview ✗ | LowConfidence, Spam | Unacceptable | 0.873 | 0.29 | 0.88 | 0.02 | 12 |
| personal-1 | personal_data | review | ManualReview | LowConfidence, Spam, PersonalData | Acceptable | 0.714 | 0.26 | 0.54 | 0.96 | 13 |
| personal-2 | personal_data | review | Reject ✗ |  | Unacceptable | 0.923 | 0.41 | 0.33 | 0.94 | 11 |
| injection-1 | injection | reject | Reject |  | Unacceptable | 0.985 | 0.81 | 0.93 | 0.03 | 11 |
| injection-2 | injection | reject | Reject |  | Unacceptable | 0.913 | 0.67 | 0.60 | 0.05 | 12 |
| long-1 | long | approve | Approve |  | Acceptable | 0.947 | 0.12 | 0.05 | 0.03 | 13 |
| tokens-1 | tokenization | reject | ManualReview ✗ | LowConfidence, Toxicity, Spam | Unacceptable | 0.892 | 0.37 | 0.71 | 0.18 | 20 |

## Thresholds

Decisions as expected / wrongly rejected / wrongly approved / manual reviews, for other approve thresholds (rows) and toxicity limits (columns); the other settings stay at their defaults.

| approve ≥ | toxicity ≤ 0.30 | toxicity ≤ 0.35 | toxicity ≤ 0.40 | toxicity ≤ 0.45 | toxicity ≤ 0.50 |
|---|---|---|---|---|---|
| 0.75 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 |
| 0.80 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 |
| 0.85 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 | 24 / 1 / 0 / 11 |
| 0.90 | 21 / 1 / 0 / 14 | 21 / 1 / 0 / 14 | 21 / 1 / 0 / 14 | 21 / 1 / 0 / 14 | 21 / 1 / 0 / 14 |

| reject ≥ | outcome |
|---|---|
| 0.80 | 27 / 3 / 0 / 4 |
| 0.85 | 28 / 2 / 0 / 5 |
| 0.90 | 24 / 1 / 0 / 11 |
| 0.95 | 23 / 0 / 0 / 14 |
