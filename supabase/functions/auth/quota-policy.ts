export const LEASE_RENEWAL_THRESHOLD_SECONDS = 60 * 60;
export const DEVICE_ACTIVITY_INTERVAL_SECONDS = 6 * 60 * 60;

export function shouldRenewLease(
  expiresAt: number,
  now: number,
  thresholdSeconds = LEASE_RENEWAL_THRESHOLD_SECONDS,
): boolean {
  return expiresAt - now <= thresholdSeconds;
}

export function shouldTouchDevice(
  lastSeenAt: number,
  now: number,
  intervalSeconds = DEVICE_ACTIVITY_INTERVAL_SECONDS,
): boolean {
  return now - lastSeenAt >= intervalSeconds;
}
