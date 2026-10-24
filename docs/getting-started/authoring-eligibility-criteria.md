# Authoring eligibility criteria

A study's eligibility criteria are written in SQL. Each criterion's SQL is stored in a FHIR `Library` resource using
the [SQLQuery](https://build.fhir.org/ig/FHIR/sql-on-fhir-v2/StructureDefinition-SQLQuery.html) profile, and the study's
`Group` references these Libraries via its `characteristic`s (see
[Creating your first study](creating-your-first-study.md)). The query module runs this SQL on a schedule and lists every
patient who may be eligible on the study's screening list.

## Where the SQL runs

The query module supports three ways of running a criterion's SQL:

- [Trino](https://trino.io/), querying FHIR resources stored as Delta Lake tables, e.g. created by
  [Pathling](https://pathling.csiro.au/). This is the setup for production-sized data.
- An embedded [DuckDB](https://duckdb.org/) reading FHIR resources straight from NDJSON files, which suits demos and
  small data sets.
- A SQL on FHIR server such as Pathling, via its
  [`$sqlquery-run`](https://build.fhir.org/ig/FHIR/sql-on-fhir-v2/OperationDefinition-SQLQueryRun.html) operation.

Trino and DuckDB are selected via `QUERY_SQL_ON_FHIR_SQL_DIALECT` (`trino` by default). A `Library` holds its SQL as
`content` with the content type `application/sql;dialect=<dialect>`, and can hold the same criterion in several dialects
side by side, e.g. `application/sql;dialect=trino` and `application/sql;dialect=duckdb`. If a `Library` has content in
the configured dialect and no `relatedArtifact` entries, its SQL is run directly against Trino or DuckDB. Otherwise,
e.g. because it depends on SQL on FHIR `ViewDefinition`s, the whole `Library` is sent to the SQL on FHIR server
configured via `SQL_ON_FHIR_URL`.

Running the SQL directly is the scalable option: all of a study's criteria are then combined into a single query that
only returns the candidates. If even one of a study's criteria needs the SQL on FHIR server, every criterion of that
study is evaluated separately and the results are merged in the query module's memory, which doesn't scale as well to
a large patient population. See [How a criterion is run](../trino/index.md#how-a-criterion-is-run) and
[Querying FHIR resources with DuckDB](../trino/index.md#querying-fhir-resources-with-duckdb) for the details.

## The result columns

A criterion's SQL returns one row per patient. The query module recognizes these columns by their name, so they must
be named exactly like this:

| Column             | Type              | Meaning                                                                   |
| ------------------ | ----------------- | ------------------------------------------------------------------------- |
| `patient_id`       | string            | The id of the `Patient` resource, without the `Patient/` prefix.          |
| `is_met`           | boolean           | Whether the criterion applies to the patient, `NULL` if unknown.          |
| `is_indeterminate` | boolean, optional | If `is_met` is `NULL`: `true` if the data is inconclusive, not missing.   |
| `result_note`      | string, optional  | A free-text explanation shown for this criterion on the screening list.   |

If a criterion's SQL doesn't return one of the optional columns, it's treated as if it had returned `NULL` for it, so
there's no need to add columns a criterion doesn't use. Any other columns are ignored.

### `is_met`: true, false or NULL

`is_met` is a three-valued result, and each value has a distinct meaning:

- **`true`**: the criterion applies to the patient, e.g. the patient has the diagnosis.
- **`false`**: the criterion definitely doesn't apply.
- **`NULL`**: the criterion can't be decided, usually because the data it needs is missing, e.g. a patient without a
  birth date for an age criterion.

`is_met` always states whether the criterion's condition is true, regardless of whether it's an inclusion or exclusion
criterion. **Write exclusion criteria un-negated**: for "no prior chemotherapy", return whether the patient *had*
chemotherapy and set `exclude` to `true` on the `Group.characteristic`. The query module then negates it:

| `is_met` | Inclusion criterion (`exclude = false`) | Exclusion criterion (`exclude = true`) |
| -------- | --------------------------------------- | -------------------------------------- |
| `true`   | met                                     | not met                                |
| `false`  | not met                                 | met                                    |
| `NULL`   | unknown                                 | unknown                                |

A study's criteria are combined with SQL's three-valued `AND`: a patient for whom at least one criterion is not met is
dropped, a patient who meets every criterion is a candidate, and a patient with no criterion that's not met but at least
one unknown is listed as well, for the study team to review. Set `QUERY_SQL_ON_FHIR_REQUIRE_ALL_CRITERIA_MET=true` to
only list patients who meet every criterion.

For every listed patient, the screening list shows each criterion as *Yes*, *No* or *Unknown*. `is_indeterminate`
only affects this display: if `is_met` is `NULL` and `is_indeterminate` is `true`, the criterion is shown as
*Indeterminate* instead of *Unknown*, meaning the data is there but doesn't allow a clear decision, e.g. two
contradicting lab results. Both count as unknown when combining the criteria. `result_note` is shown alongside the
result, which is most useful to explain *why* a criterion is indeterminate, but can be returned for any result.

### Writing the SQL

Keep these in mind:

- **Use real booleans.** `is_met` must be a `BOOLEAN`, e.g. the result of a comparison or `EXISTS`, not `0`/`1`.
- **Cover the whole patient population.** Start from the `patient` table and `LEFT JOIN` the data the criterion needs,
  so a patient without matching data gets a row with `NULL` instead of no row at all. This matters most for a study's
  first criterion: its rows are the population all other criteria are joined to, so a patient missing from it is never
  listed, while a patient missing from any later criterion is treated as unknown for it.
- **Return one row per patient.** Fully identical duplicate rows, e.g. from an `UNNEST`, are collapsed, but a patient
  with both a `true` and a `false` row is an error in the criterion. Aggregate with `GROUP BY` or use `EXISTS` instead.
- **Absence of a record isn't proof.** "No chemotherapy procedure found" may only mean that your data doesn't cover the
  patient's full history. Decide per criterion whether that should count as `false` or as `NULL`.

A criterion only needs `patient_id` and `is_met`. Missing data yields `NULL` here, since `date(NULL)` is `NULL`:

```sql
SELECT
    patient.id AS patient_id,
    date_diff('year', date(patient.birthdate), current_date) >= 18 AS is_met
FROM fhir.default.patient AS patient
```

A criterion using all four columns, which treats contradicting HbA1c results as indeterminate and explains them in a
note (Trino, against Pathling's Delta Lake tables):

```sql
SELECT
    patient.id AS patient_id,
    CASE
        WHEN count(hba1c.value) = 0 THEN NULL
        WHEN min(hba1c.value) > 7.0 THEN TRUE
        WHEN max(hba1c.value) <= 7.0 THEN FALSE
        ELSE NULL
    END AS is_met,
    count(hba1c.value) > 0 AND min(hba1c.value) <= 7.0 AND max(hba1c.value) > 7.0 AS is_indeterminate,
    CASE
        WHEN min(hba1c.value) <= 7.0 AND max(hba1c.value) > 7.0
            THEN 'Contradicting HbA1c results between ' || CAST(min(hba1c.value) AS VARCHAR) || ' and '
                || CAST(max(hba1c.value) AS VARCHAR) || ' %'
    END AS result_note
FROM fhir.default.patient AS patient
LEFT JOIN (
    SELECT observation.subject.reference AS subject_reference, observation.valueQuantity.value AS value
    FROM fhir.default.observation, UNNEST(observation.code.coding) AS observation_coding
    WHERE observation_coding.system = 'http://loinc.org' AND observation_coding.code = '4548-4'
) AS hba1c ON hba1c.subject_reference = CONCAT('Patient/', patient.id)
GROUP BY patient.id
```

Before running a new study on production data, use dry-run mode to check how many patients each criterion would list,
see [Testing criteria](../trino/index.md#testing-criteria).

### One Library per criterion, or one for all

A study can reference one `Library` per criterion, or a single `Library` whose SQL encodes all of its criteria at once
and returns one combined `is_met`. Both work the same way. Separate Libraries let the screening list show each
criterion's result individually, let the [recruitment funnel](../trino/index.md#recruitment-funnel) show how many
patients each criterion rules out, and let several studies share a criterion. A single `Library` is just as fine,
e.g. if the criteria already exist as one query, but the screening list then only shows one combined result, and
exclusion criteria have to be negated in the SQL itself, with `exclude` left at `false`:

```sql
SELECT
    patient.id AS patient_id,
    date_diff('year', date(patient.birthdate), current_date) >= 18
        AND EXISTS (
            SELECT 1
            FROM fhir.default.condition, UNNEST(condition.code.coding) AS condition_coding
            WHERE condition.subject.reference = CONCAT('Patient/', patient.id)
                AND condition_coding.system = 'http://snomed.info/sct'
                AND condition_coding.code = '10509002'
        )
        AND NOT EXISTS (
            SELECT 1
            FROM fhir.default.condition, UNNEST(condition.code.coding) AS condition_coding
            WHERE condition.subject.reference = CONCAT('Patient/', patient.id)
                AND condition_coding.system = 'http://snomed.info/sct'
                AND condition_coding.code = '91302008'
        ) AS is_met
FROM fhir.default.patient AS patient
```

## Creating the Library resources

The `Library` resources are not generated for you: create them in the list module's Trial Editor, by sending them to
the FHIR server yourself, or by syncing them from Apache Superset (see the next section).

In the Trial Editor, either select an existing `Library` for a criterion, or write the criterion's SQL directly and pick
its dialect. Submitting the study then creates a `Library` for each newly written criterion.

To create a `Library` yourself, e.g. to keep criteria under version control and reuse them across studies, `PUT` it to
the FHIR server. The query module only finds Libraries with the `sql-query` type shown below. `content.data` holds the
base64-encoded SQL, here the age criterion from above:

```json
{
  "resourceType": "Library",
  "id": "age-min-18",
  "status": "active",
  "name": "AgeMin18",
  "title": "Age ≥ 18 years",
  "type": {
    "coding": [{ "system": "https://sql-on-fhir.org/ig/CodeSystem/LibraryTypesCodes", "code": "sql-query" }]
  },
  "content": [
    {
      "contentType": "application/sql;dialect=trino",
      "data": "U0VMRUNUCiAgICBwYXRpZW50LmlkIEFTIHBhdGllbnRfaWQsCiAgICBkYXRlX2RpZmYoJ3llYXInLCBkYXRlKHBhdGllbnQuYmlydGhkYXRlKSwgY3VycmVudF9kYXRlKSA+PSAxOCBBUyBpc19tZXQKRlJPTSBmaGlyLmRlZmF1bHQucGF0aWVudCBBUyBwYXRpZW50"
    }
  ]
}
```

Then reference it from a study's `Group` as `characteristic.valueReference`, either in the Trial Editor or in a
`Group` you create yourself. The sample study in
[`sample-study.fsh`](https://github.com/open-recruit/recruit/blob/master/fhir/ig/input/fsh/sample-study.fsh) is a
complete example of a `ResearchStudy`, its `Group` and Libraries with both Trino and DuckDB content.

## Syncing criteria from Apache Superset

If your team already explores its data in [Apache Superset](https://superset.apache.org/)'s SQL Lab, the
`superset-library-sync` module can turn saved queries into `Library` resources, so criteria can be developed and tested
in SQL Lab and then used in recruIT without writing any FHIR resources by hand.

`superset-library-sync` reads every saved query directly from Superset's metadata database, regardless of which user
saved it, and syncs those whose SQL starts with
[SQL on FHIR annotations](https://build.fhir.org/ig/FHIR/sql-on-fhir-v2/StructureDefinition-SQLQuery.html#sql-annotations)
in a comment. `@name` is required, and is what the `Library`'s id is derived from, so renaming or re-creating the saved
query in Superset updates the same `Library`:

```sql
/*
@name: age-min-18
@title: Age ≥ 18 years
@description: Patients who are at least 18 years old today.
@status: active
@author: Jane Doe
*/
SELECT
    patient.id AS patient_id,
    date_diff('year', date(patient.birthdate), current_date) >= 18 AS is_met
FROM fhir.default.patient AS patient
```

- Saved queries without any annotation are skipped, those with annotations but without `@name` are skipped with a
  warning.
- `@title` and `@description` default to the saved query's name and description in Superset, `@author` defaults to the
  user who saved it, and `@status` defaults to `draft`.
- The annotation comment is removed from the stored SQL. The SQL is stored twice: as dialect-agnostic
  `application/sql`, and as `application/sql;dialect=<dialect>` with the dialect configured via `SUPERSET_SQL_DIALECT`
  (`trino` by default), which the query module runs directly if it matches its own dialect.
- Each `@relatedDependency` becomes a `relatedArtifact` of the `Library`, which means the query module sends it to the
  SQL on FHIR server instead of running it directly (see [Where the SQL runs](#where-the-sql-runs)).

The synced Libraries are criteria, not studies: select them for a study's criteria in the Trial Editor.

`superset-library-sync` (image `ghcr.io/open-recruit/recruit/superset-library-sync`) runs one sync and exits, with a
non-zero exit code if any saved query failed to sync. Run it periodically, e.g. as a Kubernetes `CronJob`, configured
via these environment variables:

| Variable                     | Description                                                                     |
| ---------------------------- | ------------------------------------------------------------------------------- |
| `SPRING_DATASOURCE_URL`      | JDBC URL of Superset's PostgreSQL metadata database.                            |
| `SPRING_DATASOURCE_USERNAME` | Its user. Read access to `saved_query`, `ab_user` and `dbs` suffices.           |
| `SPRING_DATASOURCE_PASSWORD` | Its user's password.                                                            |
| `FHIR_SERVER_BASE_URL`       | Base URL of recruIT's FHIR server, e.g. `http://fhir:8080/fhir`.                |
| `SUPERSET_SQL_DIALECT`       | Dialect of the saved queries' SQL, `trino` by default. Empty: no dialect.       |
| `SYNC_DRY_RUN`               | If `true`, only log the Libraries that would be synced, without sending them.   |

### Sending the Libraries via Kafka

If `superset-library-sync` can't reach recruIT's FHIR server directly, e.g. because Superset runs in a different
network zone, it can publish the Libraries to a Kafka topic instead. Each `Library` is published as a FHIR transaction
`Bundle` with a single `PUT` entry, which
[miracum/kafka-fhir-to-server](https://github.com/miracum/kafka-fhir-to-server) can then consume from the topic and
send to recruIT's FHIR server. The Libraries are then sent only to Kafka, not to the FHIR server.

| Variable                  | Description                                                                  |
| ------------------------- | ---------------------------------------------------------------------------- |
| `SYNC_KAFKA_ENABLED`      | Set to `true` to publish to Kafka instead of the FHIR server.                |
| `SYNC_KAFKA_TOPIC`        | Topic to publish the Library bundles to, consumed by kafka-fhir-to-server.   |
| `KAFKA_BOOTSTRAP_SERVERS` | The Kafka brokers, e.g. `kafka:9092`.                                        |
| `KAFKA_SECURITY_PROTOCOL` | `PLAINTEXT` by default, set it to `SSL` to connect via TLS.                  |

For TLS, `KAFKA_SSL_TRUST_STORE`, `KAFKA_SSL_TRUST_STORE_PASSWORD`, `KAFKA_SSL_KEY_STORE_FILE` and
`KAFKA_SSL_KEY_STORE_PASSWORD` point to PKCS12 trust and key stores. These are the same settings
kafka-fhir-to-server uses, so both can share the same Kafka configuration and secrets.
