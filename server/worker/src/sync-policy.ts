export interface SyncCursor {
  changedAt: number;
  licenseId: string;
}

export function compareSyncCursor(left: SyncCursor, right: SyncCursor): -1 | 0 | 1 {
  if (left.changedAt !== right.changedAt) return left.changedAt > right.changedAt ? 1 : -1;
  if (left.licenseId === right.licenseId) return 0;
  return left.licenseId > right.licenseId ? 1 : -1;
}

export function isAfterSyncCursor(cursor: SyncCursor, candidate: SyncCursor): boolean {
  return compareSyncCursor(candidate, cursor) > 0;
}
