# Current Monica persistence integration

The harness copies the six production Monica projects required by Repository, their build defaults, and their editor configuration from the selected checkout, then builds them with this repository's GaussDB project. It does not restore, build, or modify the original Monica checkout. Source snapshots, a commit identifier, and SHA256 manifests are generated locally and ignored by Git.

The verified run used Monica `4eb5e715f58823815363c930de5f2de4a30ec8f5`, EF Core `10.0.12`, the GaussDB provider `10.0.0`, and the supplied openGauss server on localhost:8888. It starts a complete Monica host with production Repository, UnitOfWork, audit, persistence policies, and outbox/entity projections. The only replacement inputs are test identity, application entities, and the application's DataSync policy. The outbox worker is disabled so committed snapshots can be inspected deterministically.

Set `GAUSSDB_TEST_CONNECTION` in the shell, then run:

```powershell
powershell -File validation/monica/Run-Validation.ps1 -MonicaRoot D:/Documents/Code/FIPS/MoLibrary
```

The account must be able to create `bulk_monica_current20261021`. Each run resets only that dedicated database's `CurrentRows` and `MonicaOutbox` tables. Run this harness in one process at a time. A new snapshot requires a fresh validation directory/checkout; the script never removes an existing source snapshot automatically.

The seven scenarios cover:

- Native create/update/soft-delete: creation and modification identity, timestamps, DataSync policy, concurrency-stamp rotation, entity version, and exactly three durable snapshots.
- `BulkSaveChanges` preserving the production save pipeline for insertion and exactly one projection.
- Preserved bulk-save modification and physical retention of soft-deleted rows.
- Business/outbox rollback after an early save.
- Rejection of stale concurrency stamps.
- Rejection of a save after the UnitOfWork scope becomes terminal.
- Explicit binary COPY joining the current Monica transaction and rolling back with it.

`baseline-before-fix.json` records the initial result: four passed and three failed. The failure modes were a second insertion at UnitOfWork flush, a second update failing concurrency checks, and a stale update succeeding. `verified-results.json` records seven passing scenarios after callers select `new BulkConfig { PreserveSaveChangesPipeline = true }`.

This setting runs normal EF save commands, including application overrides and interceptors. It preserves framework behavior and does not promise COPY throughput. Explicit bulk operations retain their high-throughput path and remain application-owned direct writes: they do not apply Monica audit, soft-delete rewriting, persistence policies, or automatic entity projections. Use them only when those effects are deliberately handled by the importing operation. A blanket replacement of Monica or FIPS `SaveChanges` with the default bulk-save path is unsafe.

The application's model selects `UseSerialColumns()` because the supplied openGauss server does not accept the provider's default PostgreSQL identity-column DDL. Business audit instants use `timestamp with time zone`; the unchanged Monica outbox mappings preserve their documented UTC clock-value conversion.
