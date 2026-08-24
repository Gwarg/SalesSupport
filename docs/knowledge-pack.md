# Knowledge pack — SQLite schema and contracts

**Status:** draft for review, 2026-08-24. Drill-down of D4, D5, D8, D13, D21, D28. The pack is the contract between the ingestion pipeline (writer) and the backend (reader). One self-contained file per company per version: `{company}_{version}.pack.sqlite`.

## Consumers

1. **Retrieval** (D13): hybrid search over product cards and family summaries, per thread.
2. **Catalog map** (D4): the rendered in-context map the advisor always sees.
3. **Question maps** (D4): per-family discovery-question material in the advisor's company block.
4. **Alias resolution** (D28): order lines and spoken names → `product_ref`. Used by the brief adapter *and* by the orchestrator when the gate reports a `name_as_said` (the gate stays dumb; code resolves).
5. **Relations**: successor/accessory/complement graph — powers the advisor's "upgrades only for owned products" and cross-sell rules.
6. **STT vocabulary** (D8): phrase list pushed to the STT session at call start.

## Principles

- **Immutable artifact.** The backend opens it read-only (`immutable=1`); nothing ever writes to a published pack. New version = new file, atomically swapped.
- **Stable IDs across versions.** `prod:{normalized-sku}` and `fam:{slug}` are derived deterministically from source data, so a picture from an ongoing call still resolves after a nightly pack swap, and CRM order-line resolution (D28) survives rebuilds.
- **Canonical content language per company** (meta `content_language`, e.g. `sv`). Cards and summaries are written once, in one language; English-language calls rely on cross-lingual vectors for retrieval and on the advisor for output language (it composes questions/answers in `{call_language}` regardless of card language). Per-language content generation is a recorded upgrade path, not v1.
- **Embeddings are local, everywhere.** A multilingual ONNX model (v1: `multilingual-e5-small`, 384 dims) runs in the pipeline (passage embedding) and in the backend (query embedding). Same model both sides — recorded in meta as a hard contract. No cloud embedding vendor: removes a third vendor, works for on-prem installations (D14/D20), and e5's cross-lingual training is what makes canonical-language content searchable from English queries. Note: e5 requires `"query: "` / `"passage: "` text prefixes — `embedding_scheme` in meta captures this.
- **No native SQLite extensions.** Vectors are float32 little-endian BLOBs; the backend loads them into one matrix and brute-forces cosine similarity in memory (`TensorPrimitives`). At ≤ ~20k vectors this is sub-millisecond; `sqlite-vec` is the documented fallback if a future catalog is 10× bigger. FTS5 ships in the standard `e_sqlite3` bundle — no extra dependency.

## Schema (DDL)

```sql
CREATE TABLE meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE families (
  id           TEXT PRIMARY KEY,          -- fam:handheld-scanners
  parent_id    TEXT REFERENCES families(id),
  name         TEXT NOT NULL,
  path         TEXT NOT NULL,             -- "Hårdvara > Skannrar > Handhållna"
  summary      TEXT NOT NULL,             -- the paragraph the catalog map is built from
  question_map TEXT,                      -- markdown: discovery questions for this family
  embedding    BLOB NOT NULL
);

CREATE TABLE products (
  id             TEXT PRIMARY KEY,        -- prod:x60
  sku            TEXT NOT NULL,
  name           TEXT NOT NULL,
  family_id      TEXT NOT NULL REFERENCES families(id),
  status         TEXT NOT NULL DEFAULT 'active',   -- active | discontinued
  attributes     TEXT NOT NULL DEFAULT '{}',       -- JSON: typed key specs
  price_amount   REAL,                    -- snapshot, indicative (D6)
  price_currency TEXT,
  price_note     TEXT,                    -- "per unit, excl. VAT"
  availability   TEXT,                    -- snapshot hint, e.g. "normally in stock"
  card           TEXT NOT NULL,           -- enriched markdown product card (D4)
  embedding      BLOB NOT NULL,
  source_ref     TEXT                     -- provenance: feed row / document
);
CREATE INDEX idx_products_family ON products(family_id);
CREATE INDEX idx_products_sku    ON products(sku);

CREATE TABLE aliases (
  alias     TEXT NOT NULL,                -- "X-40", legacy SKU, spoken name
  kind      TEXT NOT NULL,                -- sku | name | spoken | legacy
  target_id TEXT NOT NULL,                -- prod:* or fam:*
  PRIMARY KEY (alias, target_id)
);

CREATE TABLE relations (
  from_id TEXT NOT NULL,
  to_id   TEXT NOT NULL,
  kind    TEXT NOT NULL,   -- successor_of | accessory_of | complement_of | consumable_for | variant_of
  note    TEXT,
  PRIMARY KEY (from_id, to_id, kind)
);
-- (prod:x60, prod:x40, successor_of) reads "X60 is the successor of X40".

CREATE TABLE catalog_map (
  tier           TEXT PRIMARY KEY,        -- 'full' (v1); 'compact' reserved for
  text           TEXT NOT NULL,           --   small-context local providers (D14)
  token_estimate INTEGER NOT NULL
);

CREATE TABLE stt_vocab (
  term   TEXT PRIMARY KEY,
  weight REAL NOT NULL DEFAULT 1.0
);

CREATE VIRTUAL TABLE search_fts USING fts5(
  body,                                   -- name + aliases + card/summary + flattened attributes
  doc_id UNINDEXED,                       -- prod:* or fam:*
  kind   UNINDEXED,                       -- product | family
  tokenize = 'unicode61 remove_diacritics 2'
);
```

No stemming (porter is English-only; unicode61 treats sv/en evenly) — BM25 works on near-exact tokens, vectors carry the semantics.

### Meta keys

| Key | Example | Notes |
|---|---|---|
| `schema_version` | `1` | breaking pack-format changes bump this; backend refuses unknown majors |
| `company_id` | `nordfrys` | |
| `pack_version` | `2026-08-24.1` | monotonic; shown in admin/logs |
| `built_at` | ISO 8601 | |
| `feed_snapshot` | hash/date of source export | audit trail |
| `content_language` | `sv` | canonical card/summary language |
| `embedding_model` | `multilingual-e5-small` | hard contract with backend |
| `embedding_dims` | `384` | validated against BLOB lengths |
| `embedding_scheme` | `e5-prefix` | query/passage prefix convention |
| `count_products` / `count_families` | `9412` / `214` | validated against tables |

### Product card format (the `card` column)

Markdown, written by the enrichment pass, ~150–400 words. Fixed section order so the advisor's attention lands predictably:

```markdown
# X60 handskanner
Tålig handdatorskanner för lager och kyl/frys, ersätter X40-serien.

**Passar:** lager med kyl- eller fryszoner, 1–2-skift, handskvänlig.
**Nyckelspecar:** drifttemp -30…+50 °C · IP67 · batteri 14 h kallmiljö ·
laddas i samma dockor som X40.
**Skiljer sig från:** X50 (ej frysklassad), X40 (äldre batteriteknik).
**Säljs ofta med:** serviceavtal, extra batteri, dockstation LP-dock.
**Att fråga kunden:** antal enheter i kalla zoner? skifttider? befintliga dockor?
```

Key specs must also land as typed JSON in `attributes` (`{"operating_temp_c": [-30, 50], "ip_rating": "IP67", ...}`) — that's what answers the ask lane's spec questions without a datasheet-chunk table.

## Retrieval flow (backend, code)

1. Query text = `advice.topics` hint (proactive) or the typed query (on-demand).
2. Embed query locally (`query: ` prefix) + run FTS5 match (sanitized, OR-joined terms).
3. Merge with reciprocal rank fusion (k = 60) — no tuned weights in v1.
4. Optional boost list: family IDs derived from `product_interest` (owned/interested products' families, D28) get a rank bonus.
5. Return top `{retrieval_k=4}` rows per thread — mixed product cards and family summaries (family hits matter when the customer's need is category-level: "something for cold storage").

**Alias resolution** (`Resolve(text) → id | ambiguous | none`): exact alias match → normalized match (casefold, diacritics, dash/space folding) → FTS fallback. Multiple candidates ⇒ `ambiguous`: the caller stores `product_ref: null` rather than guessing — a wrong resolution poisons stance rules (D28, picture doc).

## Load contract (backend)

- Open read-only with `immutable=1`; never write; no WAL.
- On load: verify `schema_version` + embedding meta against the local embedder; load hot set into RAM — products (all columns incl. cards: ~20 MB at 10k SKUs), embedding matrix (~15 MB at 384 dims), aliases dictionary, relations lookup, families, catalog map. SQLite stays on disk as the authority; RAM is a cache of all of it.
- **Atomic swap:** new file → validate + load into a fresh in-memory pack object → swap the reference → old object released when in-flight calls finish. Active calls keep the pack version they started with.

**Size estimate at 10k SKUs:** cards ~20 MB, FTS ~35 MB, vectors ~15 MB, rest ~5 MB → **~75 MB per pack**. Trivial to ship, version, and keep N generations of.

## Pipeline validation (build fails, never runtime surprises)

- Referential integrity: every product→family, every relation/alias target exists; family tree acyclic, single root.
- Every product has non-empty `card`, valid `attributes` JSON, embedding of exactly `embedding_dims` × 4 bytes.
- `search_fts` has exactly one row per product + per family.
- `catalog_map('full')` present and `token_estimate` within the configured budget.
- Counts match meta; `stt_vocab` non-empty; alias set contains every SKU and product name.

## Open items

1. **`doc_chunks` table** (datasheet passages for deep spec Q&A) — only if L0 shows `attributes` + cards can't answer the ask lane's spec questions. Don't build speculatively.
2. **`catalog_map('compact')` tier** for small-context local providers — generate when the first local installation is real.
3. **Multi-currency / per-market pricing** — v1 assumes one currency per company; revisit when a launch company quotes in EUR and SEK.
4. **Per-language content** (dual sv/en cards) — only if cross-lingual retrieval quality on English calls disappoints in L0.
