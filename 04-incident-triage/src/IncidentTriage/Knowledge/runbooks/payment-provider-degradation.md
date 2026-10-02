# Runbook: Payment provider degradation

Owner: Payments team. Applies to payments-gateway and checkout-api's PaymentClient.

## Symptoms
- Payment authorisations failing or slow; `PaymentProviderException` / HTTP 502 / 504 from the provider.
- Customer reports "payment failed" or "charged twice".
- Provider status page may lag behind reality by 10-20 minutes.

## Likely causes
- Provider brownout or regional outage (check their status page and our provider latency dashboard).
- Retry storm: our client retries too aggressively and amplifies the provider's degradation.
- Expired or rotated API credentials (HTTP 401 / 403 from the provider).

## Mitigation
- Fail over to the secondary provider via the `payments.secondary-provider` feature flag.
- Reduce retry count and add exponential back-off with jitter.
- Never retry a payment authorisation without an idempotency key (risk of double charges).
