# Decision model quality report

Model version: `typesafe/jev-1.14-20261014@1.14-20261014+cef8109c93be`. Comments: 32, evaluated: 32, failed: 0.
Default policy: approve ≥ 0.85, reject ≥ 0.85, toxicity ≤ 0.35, spam ≤ 0.5, personal data ≤ 0.5.

## Summary at the default policy

- Decision as expected: 30 of 32
- Verdict label right (expected approve/reject, any confidence): 27 of 27
- **Wrongly rejected** (expected approve or review, got Reject): 2 of 32
- **Wrongly approved** (expected reject or review, got Approve): 0 of 32
- Sent to manual review: 3 of 32
- Latency: median 0.4 s, max 0.8 s, total 0.2 min.

Expected (rows) against decided (columns):

| expected | Approve | Reject | ManualReview |
|---|---|---|---|
| approve | 11 | 0 | 0 |
| reject | 0 | 16 | 0 |
| review | 0 | 2 | 3 |

## Comments

| id | category | expected | decision | reasons | label | confidence | toxicity | spam | personal data | seconds |
|---|---|---|---|---|---|---|---|---|---|---|
| praise-1 | praise | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.02 | 0.02 | 0.8 |
| praise-2 | praise | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.03 | 0.02 | 0.4 |
| praise-3 | praise | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.02 | 0.02 | 0.4 |
| praise-4 | praise | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.03 | 0.02 | 0.5 |
| criticism-1 | criticism | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.02 | 0.02 | 0.4 |
| criticism-2 | criticism | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.03 | 0.02 | 0.4 |
| criticism-3 | criticism | approve | Approve |  | Acceptable | 0.8900 | 0.22 | 0.02 | 0.02 | 0.4 |
| criticism-4 | criticism | approve | Approve |  | Acceptable | 1.0000 | 0.03 | 0.02 | 0.02 | 0.4 |
| criticism-5 | criticism | approve | Approve |  | Acceptable | 1.0000 | 0.05 | 0.02 | 0.02 | 0.4 |
| criticism-6 | criticism | approve | Approve |  | Acceptable | 1.0000 | 0.09 | 0.02 | 0.02 | 0.4 |
| rude-1 | rude | review | ManualReview | LowConfidence, Toxicity | Unacceptable | 0.8300 | 0.43 | 0.03 | 0.02 | 0.4 |
| rude-2 | rude | review | Reject ✗ |  | Unacceptable | 0.9400 | 0.51 | 0.03 | 0.02 | 0.4 |
| rude-3 | rude | review | ManualReview | LowConfidence, Toxicity | Unacceptable | 0.6400 | 0.36 | 0.20 | 0.02 | 0.4 |
| insult-1 | insult | reject | Reject |  | Unacceptable | 1.0000 | 0.66 | 0.03 | 0.02 | 0.4 |
| insult-2 | insult | reject | Reject |  | Unacceptable | 1.0000 | 0.66 | 0.03 | 0.02 | 0.4 |
| insult-3 | insult | reject | Reject |  | Unacceptable | 0.9900 | 0.66 | 0.04 | 0.02 | 0.5 |
| insult-4 | insult | reject | Reject |  | Unacceptable | 1.0000 | 0.66 | 0.03 | 0.02 | 0.4 |
| vulgar-1 | vulgar | reject | Reject |  | Unacceptable | 0.9800 | 0.41 | 0.04 | 0.02 | 0.4 |
| vulgar-2 | vulgar | reject | Reject |  | Unacceptable | 0.9800 | 0.63 | 0.03 | 0.02 | 0.4 |
| threat-1 | threat | reject | Reject |  | Unacceptable | 1.0000 | 1.00 | 0.03 | 0.10 | 0.4 |
| threat-2 | threat | reject | Reject |  | Unacceptable | 1.0000 | 0.92 | 0.03 | 0.02 | 0.5 |
| hate-1 | hate | reject | Reject |  | Unacceptable | 1.0000 | 0.99 | 0.03 | 0.02 | 0.4 |
| hate-2 | hate | reject | Reject |  | Unacceptable | 1.0000 | 0.88 | 0.03 | 0.02 | 0.4 |
| spam-1 | spam | reject | Reject |  | Unacceptable | 1.0000 | 0.01 | 0.99 | 0.06 | 0.4 |
| spam-2 | spam | reject | Reject |  | Unacceptable | 1.0000 | 0.03 | 0.99 | 0.07 | 0.4 |
| spam-3 | spam | reject | Reject |  | Unacceptable | 1.0000 | 0.02 | 0.99 | 0.09 | 0.5 |
| personal-1 | personal_data | review | ManualReview | LowConfidence, PersonalData | Unacceptable | 0.8000 | 0.00 | 0.34 | 0.99 | 0.4 |
| personal-2 | personal_data | review | Reject ✗ |  | Unacceptable | 0.9600 | 0.06 | 0.20 | 0.99 | 0.4 |
| injection-1 | injection | reject | Reject |  | Unacceptable | 1.0000 | 0.86 | 0.04 | 0.03 | 0.5 |
| injection-2 | injection | reject | Reject |  | Unacceptable | 0.9200 | 0.64 | 0.05 | 0.03 | 0.4 |
| long-1 | long | approve | Approve |  | Acceptable | 1.0000 | 0.00 | 0.02 | 0.04 | 0.4 |
| tokens-1 | tokenization | reject | Reject |  | Unacceptable | 1.0000 | 0.03 | 0.99 | 0.92 | 0.4 |

## Thresholds

Decisions as expected / wrongly rejected / wrongly approved / manual reviews, for other approve thresholds (rows) and toxicity limits (columns); the other settings stay at their defaults.

| approve ≥ | toxicity ≤ 0.30 | toxicity ≤ 0.35 | toxicity ≤ 0.40 | toxicity ≤ 0.45 | toxicity ≤ 0.50 |
|---|---|---|---|---|---|
| 0.75 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 |
| 0.80 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 |
| 0.85 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 | 30 / 2 / 0 / 3 |
| 0.90 | 29 / 2 / 0 / 4 | 29 / 2 / 0 / 4 | 29 / 2 / 0 / 4 | 29 / 2 / 0 / 4 | 29 / 2 / 0 / 4 |

| reject ≥ | outcome |
|---|---|
| 0.80 | 28 / 4 / 0 / 1 |
| 0.85 | 30 / 2 / 0 / 3 |
| 0.90 | 30 / 2 / 0 / 3 |
| 0.95 | 30 / 1 / 0 / 5 |
