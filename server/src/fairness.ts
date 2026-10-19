// Private Roulette — provable fairness.
// result = uint32_be(sha256(`${dice_roll}:${control_number}`)[0..8]) % 37
// Verified against a live SimpleRoulette capture (see PROTOCOL.md) and kept
// identical so existing player expectations of the formula carry over.

const RED = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

export const WHEEL_ORDER = [
  0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
  5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
];

export function colorOf(n: number): "red" | "black" | "green" {
  if (n === 0) return "green";
  return RED.has(n) ? "red" : "black";
}

export async function sha256Hex(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export interface Proof {
  dice_roll: number | null;
  control_number: string;
  hash: string; // first 16 hex chars of the full sha256
  seed: number; // uint32 from the first 8 hex chars
  result: number;
}

export async function computeResult(diceRoll: number | null, controlNumber: string, forcedNumber: number | null): Promise<{ result: number; proof: Proof }> {
  if (forcedNumber !== null && forcedNumber !== undefined) {
    return {
      result: forcedNumber,
      proof: { dice_roll: diceRoll, control_number: controlNumber, hash: "manual", seed: 0, result: forcedNumber },
    };
  }
  if (diceRoll === null || diceRoll === undefined) throw new Error("missing dice roll");
  const full = await sha256Hex(`${diceRoll}:${controlNumber}`);
  const seed = parseInt(full.slice(0, 8), 16) >>> 0;
  const result = seed % 37;
  return { result, proof: { dice_roll: diceRoll, control_number: controlNumber, hash: full.slice(0, 16), seed, result } };
}

export function cryptoRandomHex(len: number): string {
  const bytes = new Uint8Array(len / 2);
  crypto.getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function cryptoInt(min: number, max: number): number {
  const range = max - min + 1;
  return min + (crypto.getRandomValues(new Uint32Array(1))[0] % range);
}

// ---- bet semantics (identical to the documented table) ----

function dozenOf(n: number): number | null {
  if (n === 0) return null;
  return Math.floor((n - 1) / 12) + 1;
}

function colOf(n: number): number | null {
  if (n === 0) return null;
  return n % 3 === 0 ? 3 : n % 3;
}

export function payoutMultiplierForTarget(target: string, result: number): number {
  const t = (target || "").trim().toLowerCase();

  if (t.startsWith("n-")) {
    const n = parseInt(t.slice(2), 10);
    return Number.isFinite(n) && result === n ? 36 : 0;
  }
  switch (t) {
    case "red": return result === 0 ? 0 : colorOf(result) === "red" ? 2 : 0;
    case "black": return result === 0 ? 0 : colorOf(result) === "black" ? 2 : 0;
    case "odd": return result === 0 ? 0 : result % 2 === 1 ? 2 : 0;
    case "even": return result === 0 ? 0 : result % 2 === 0 ? 2 : 0;
    case "low": return result >= 1 && result <= 18 ? 2 : 0;
    case "high": return result >= 19 && result <= 36 ? 2 : 0;
    case "firstfour": return result >= 0 && result <= 3 ? 9 : 0;
  }
  const list = (prefix: string, count: number): number[] | null => {
    if (!t.startsWith(prefix)) return null;
    const parts = t.slice(prefix.length).split("-");
    if (parts.length !== count) return null;
    const nums = parts.map((p) => parseInt(p, 10));
    if (nums.some((n) => !Number.isFinite(n) || n < 0 || n > 36)) return null;
    return nums;
  };
  const split = list("split-", 2);
  if (split) return split.includes(result) ? 18 : 0;
  const street = list("street-", 3);
  if (street) return street.includes(result) ? 12 : 0;
  const corner = list("corner-", 4);
  if (corner) return corner.includes(result) ? 9 : 0;
  const six = list("sixline-", 6);
  if (six) return six.includes(result) ? 6 : 0;
  if (t.startsWith("dozen-")) {
    const d = parseInt(t.slice(6), 10);
    return d >= 1 && d <= 3 && dozenOf(result) === d ? 3 : 0;
  }
  if (t.startsWith("col-")) {
    const c = parseInt(t.slice(4), 10);
    return c >= 1 && c <= 3 && colOf(result) === c ? 3 : 0;
  }
  return 0;
}

export function isInsideBet(target: string): boolean {
  const t = target.toLowerCase();
  return (
    t.startsWith("n-") ||
    t.startsWith("split-") ||
    t.startsWith("street-") ||
    t.startsWith("corner-") ||
    t.startsWith("sixline-") ||
    t === "firstfour"
  );
}

export function isValidBetTarget(target: string): boolean {
  const t = (target || "").trim().toLowerCase();
  if (["red", "black", "odd", "even", "low", "high", "firstfour"].includes(t)) return true;
  const list = (prefix: string, count: number): boolean => {
    if (!t.startsWith(prefix)) return false;
    const parts = t.slice(prefix.length).split("-");
    if (parts.length !== count) return false;
    return parts.every((p) => {
      const n = parseInt(p, 10);
      return Number.isFinite(n) && n >= 0 && n <= 36;
    });
  };
  if (list("split-", 2) || list("street-", 3) || list("corner-", 4) || list("sixline-", 6)) return true;
  if (t.startsWith("dozen-")) {
    const d = parseInt(t.slice(6), 10);
    return d >= 1 && d <= 3;
  }
  if (t.startsWith("col-")) {
    const c = parseInt(t.slice(4), 10);
    return c >= 1 && c <= 3;
  }
  return false;
}

const PLAYER_COLORS = [
  "#4A90E2", "#E74C3C", "#2ECC71", "#9B59B6", "#F39C12",
  "#1ABC9C", "#E91E63", "#3498DB", "#16A085", "#D35400",
];

export function playerColor(id: string): string {
  let h = 0;
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0;
  return PLAYER_COLORS[h % PLAYER_COLORS.length];
}
