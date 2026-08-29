import fs from "node:fs";
import path from "node:path";

const [inputPath, outputPath] = process.argv.slice(2);
if (!inputPath || !outputPath) {
  console.error("Usage: node convert-d1-export-to-postgres.mjs <d1-export.sql> <postgres-migration.sql>");
  process.exit(2);
}

const targetTables = ["licenses", "devices", "leases", "verification_logs", "request_nonces"];
const rows = Object.fromEntries(targetTables.map((table) => [table, []]));

function splitValues(valueText) {
  const values = [];
  let start = 0;
  let inString = false;
  for (let index = 0; index < valueText.length; index += 1) {
    const character = valueText[index];
    if (character !== "'") {
      if (character === "," && !inString) {
        values.push(valueText.slice(start, index).trim());
        start = index + 1;
      }
      continue;
    }
    if (inString && valueText[index + 1] === "'") {
      index += 1;
      continue;
    }
    inString = !inString;
  }
  if (inString) throw new Error("Unterminated SQL string literal");
  values.push(valueText.slice(start).trim());
  return values;
}

function unquoteColumn(value) {
  const match = value.trim().match(/^"([^"]+)"$/);
  if (!match) throw new Error(`Unexpected column token: ${value}`);
  return match[1];
}

function normalizeValue(table, column, token) {
  if (table === "verification_logs" && column === "success") {
    if (token === "0") return "FALSE";
    if (token === "1") return "TRUE";
    throw new Error(`Unexpected verification_logs.success value: ${token}`);
  }
  if (table === "verification_logs" && column === "details_json" && token !== "NULL") {
    return `${token}::jsonb`;
  }
  return token;
}

const source = fs.readFileSync(path.resolve(inputPath), "utf8");
for (const line of source.split(/\r?\n/)) {
  if (!line.startsWith("INSERT INTO \"")) continue;
  const match = line.match(/^INSERT INTO "([^"]+)" \((.*)\) VALUES\((.*)\);$/);
  if (!match || !rows[match[1]]) continue;
  const table = match[1];
  const columns = match[2].split(",").map(unquoteColumn);
  const rawValues = splitValues(match[3]);
  if (columns.length !== rawValues.length) {
    throw new Error(`${table}: expected ${columns.length} values, got ${rawValues.length}`);
  }
  rows[table].push({
    columns,
    values: rawValues.map((token, index) => normalizeValue(table, columns[index], token)),
  });
}

const output = [
  "-- Generated from a Cloudflare D1 export.",
  "-- Source database is never modified by this file.",
  "BEGIN;",
];

for (const table of targetTables) {
  for (const row of rows[table]) {
    output.push(
      `INSERT INTO license.${table} (${row.columns.join(", ")}) VALUES (${row.values.join(", ")}) ON CONFLICT DO NOTHING;`,
    );
  }
}

output.push("COMMIT;", "");
fs.writeFileSync(path.resolve(outputPath), output.join("\n"), "utf8");
console.log(JSON.stringify({
  output: path.resolve(outputPath),
  counts: Object.fromEntries(targetTables.map((table) => [table, rows[table].length])),
}));
