# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

- **Primary: study teams.** Study nurses, study coordinators and investigators, mostly at German
  university hospitals. They work on desktop workstations and fit screening in between clinical
  duties. Their job is to go through a trial's candidate patients, check each one's clinical summary
  against the eligibility criteria, decide whether to approach them, and move them from candidate
  through screening to on-study. They also keep notes and get notified when new candidates turn up.
- **Secondary: data integration center (DIC) staff.** They deploy and run recruIT, write each
  trial's inclusion and exclusion criteria as SQL, publish them to the FHIR server, and decide who
  can see each screening list. In the app they hold an admin role (`Auth:AdminRoles`).

## Product Purpose

recruIT helps hospitals recruit patients for clinical trials. On a schedule it checks routine
clinical data for patients who meet a trial's eligibility criteria, puts them on that trial's
screening list, and notifies the study team. A person always makes the screening decision. The
software narrows the search; it never enrolls anyone itself.

Success means study teams find eligible patients they would otherwise have missed, spend less
time searching charts, and can trust why each patient is (or isn't) on the list.

## Positioning

Two things matter equally, and future work should not trade one for the other:

1. **Open and built on standards.** recruIT is open source and FHIR-native. Eligibility criteria are
   portable SQL-on-FHIR `Library` resources that can hold the same query in several SQL dialects
   (Trino, DuckDB, a SQL on FHIR server) and can be shared between studies. It runs on the
   hospital's own infrastructure. The architecture is peer-reviewed
   (DOI 10.1016/j.compbiomed.2024.108411) and follows Trinczek et al.'s generic recruitment
   architecture.
2. **Transparent screening with a person deciding.** Status is shown per criterion. Patients whose
   data is missing stay on the list as *indeterminate* for the team to review instead of being
   dropped without notice. The attrition funnel shows how many patients each criterion rules out.
   The software recommends; the study team decides.

## Operating Context

- Study teams use the `list` web app: the dashboard of their trials, a trial's screening list,
  the patient dialog (clinical summary, criteria met/not met/indeterminate, notes, change history),
  and the notification list. Notifications also go out by email, either right away or as a daily,
  weekly or monthly digest.
- DIC staff use the trial editor and criteria manager. They write new criteria or pick from the
  library catalog, can bring in saved SQL from Apache Superset (`superset-library-sync`), and manage
  per-trial access.
- Behind the app: a HAPI FHIR server, the `query-sql-on-fhir` module that runs the criteria SQL,
  PostgreSQL, an OIDC provider for sign-in, and SMTP for email. It is deployed with Docker Compose
  or the Helm chart.
- The documentation site (openrecruit.dev, built with Zensical) is how hospitals evaluate,
  install and configure recruIT.

## Capabilities and Constraints

- **Localization:** the app UI ships in English and German, and the two must stay at parity. Every
  user-facing string goes through `SharedResources` (`.resx` / `.de.resx`). Layouts must fit
  German text, which often runs longer than English.
- **Access:** OIDC sign-in; each screening list is granted per user; admin-only areas check the
  admin roles.
- **Recruitment statuses and criterion states** (met / not met / indeterminate) are product
  terminology. Keep the wording consistent across the app, emails and docs.
- **Current implementation (a fact, not a requirement):** Blazor Server with BlazorBlueprint
  components and a precompiled CSS bundle, so Tailwind utilities that the bundle doesn't already
  contain don't work. No requirement was set to keep or replace this stack.
- **Undecided:** no accessibility standard (such as WCAG 2.1 AA or BITV 2.0) has been set as a
  requirement.

## Brand Commitments

- Name: **recruIT**, written exactly that way (lowercase "recru", uppercase "IT").
- Existing logo assets: horizontal logo, mark and app icon, each in light and dark variants
  (`docs/_img/logo/`, `src/list/RecruIT.List/wwwroot/img/mark-*.png`).
- Voice in existing copy: plain and factual, no hype. It describes what the software does
  ("Find eligible patients for clinical trials, automatically").

## Evidence on Hand

- Peer-reviewed publication: DOI 10.1016/j.compbiomed.2024.108411 (`CITATION.cff`).
- Product screenshots: screening list, attrition funnel, notification list, trial editor
  (`docs/_img/screenshots/`).
- Synthetic patient data for demos and development (Synthea, `src/hack/synthea`).
- Security and supply-chain evidence: OpenSSF Scorecard, SLSA level 3 provenance, Helm chart
  hardened following the NSA/CISA Kubernetes Hardening Guidance.
- **Absent, do not invent:** named hospital deployments, customer testimonials, recruitment
  outcome metrics or benchmarks, and pricing (recruIT is open source; see `LICENSE`).

## Product Principles

1. **The team decides; the software recommends.** Never present a recommendation as a decision,
   and never hide a patient because the system wasn't sure.
2. **Show why.** Every candidate's place on the list should trace back to specific criteria and
   their state.
3. **Fit into clinical work.** Study teams screen between other duties: get them to the next
   decision fast and keep their place.
4. **Open by default.** Use portable standards (FHIR, SQL-on-FHIR) and infrastructure the hospital
   controls over anything proprietary or hosted.
5. **English and German are both first-class.** Neither language should read like an afterthought.
