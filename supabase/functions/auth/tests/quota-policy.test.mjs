import test from "node:test";
import assert from "node:assert/strict";
import {
  shouldRenewLease,
  shouldTouchDevice,
} from "../quota-policy.ts";

test("a normal heartbeat does not renew a healthy lease", () => {
  assert.equal(shouldRenewLease(10_000, 1_000), false);
});

test("a heartbeat renews only within the one-hour threshold", () => {
  assert.equal(shouldRenewLease(4_600, 1_000), true);
  assert.equal(shouldRenewLease(4_601, 1_000), false);
});

test("device activity writes are limited to six-hour intervals", () => {
  assert.equal(shouldTouchDevice(1_000, 22_599), false);
  assert.equal(shouldTouchDevice(1_000, 22_600), true);
});
