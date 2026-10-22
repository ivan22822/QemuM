# Подключение службы снимков к интерфейсу

`src/gui/SnapshotsUi.cs` содержит представления и `ISnapshotUiService`. Чтобы подключить новую службу, реализовать этот интерфейс и передать экземпляр в `SnapshotsWindow`. Представления не запускают QEMU/qemu-img, не выбирают RPC-команды, не пишут каталог и настройки.

## Сервис

- `Supports(feature)` сообщает подтверждённую поддержку возможности. Текущее состояние и принадлежность дисков проверяются отдельно на стороне службы при каждом Execute.
- `Load()` асинхронно возвращает каталог. Не выполнять запись в диски при чтении.
- `Execute(SnapshotUiRequest)` возвращает подтверждённый результат или исключение. Отсутствие исключения означает завершение, а не только постановку задачи в очередь. Для фонового worker адаптер должен дождаться окончательного результата.
- `Progress` передаёт OperationId, Phase, опциональные Done/Total одной единицы измерения и CanCancel. Отмена не означает откат уже записанных данных. После неё исходная Execute завершается подтверждённым результатом/ошибкой.

Capabilities: `refresh`, `create_live`, `create_offline`, `metadata`, `ram`, `external`, `config`, `guest_freeze`, `restore_start`, `restore`, `restore_config`, `restore_ram`, `restore_external`, `delete`, `protect`, `export`, `schedule`, `recover`, `cancel`.

После этапа 1–10 `LegacySnapshotUiService` читает capabilities из каталога. Создание использует snapshot_plan → отдельное подтверждение точных ресурсов → snapshot_execute. Подключены metadata_on_create (название/описание при создании), recover (сверка незавершённого создания) и adopt (явное принятие старого одиночного снимка). Откат/запуск по-прежнему идёт через start, теперь с UUID операции и журналом.

В этапах 11–20 включены metadata/protect: snapshot_metadata получает name VM, group_id, revision каталога, action edit/protect и options. UI сохраняет revision при загрузке; backend отвергает устаревшую редакцию и неизвестный UUID. Изменение label/description/protected не переименовывает внутренний тег и не пишет образы.

snapshot_plan выбирает live/offline/RAM/external по проверенному состоянию и Options.kind; snapshot_execute принимает только ID серверного плана. snapshot_offline_plan дополнительно требует выключенной VM и возвращает can_publish=true после подготовки полноценного серверного плана. snapshot_operation_plan формирует подтверждаемый план offline-delete/offline-restore (включая выбранный restore_scope и config_diff) либо переключение внешней точки. UI отдельно выполняет обычный start после подтверждённого отката; ошибка запуска не повторяет откат.

snapshot_ram_restore получает group_id и start_after. Существующая VM должна быть на паузе; выключенная запускается с -S до проверок. После загрузки без start_after остаётся на паузе. RAM capability доступна только при паузе TCG; восстановление дополнительно проверяет fingerprint actual command/CPU/RAM/machine/build. WHPX-RAM не включается.

snapshot_schedule сохраняет options и revision; фактический цикл работает только пока открыт backend приложения. Ручные, защищённые, неполные, RAM и внешние точки исключены из очистки. snapshot_chain_plan — только read-only план commit/rebase/merge с executable=false. Внешний каталог передаёт default_snapshot_kind=external. Полная матрица/ограничения — в SNAPSHOT-11-20-PROGRESS.md.

## Каталог

Корень: `rows`, `online`, `vm_state` (running/paused/prelaunch/shutdown/stopped/unknown), `context` (текущие параметры VM), `recovery_required`, необязательные `history`, `operations`, `schedule`.

Строка: `id` (стабильный ID), `name` (внутреннее имя), `label`, `description`, `date` (UTC Unix seconds), `kind` (disk/ram/external), `state` (ready/partial/missing/incompatible/uncertain/recovery-required), `protected`, `checkpoint`, `disks` (массив строк или объектов path/name), `parameters`, `reason`. Дополнительные понятные пояснения: `size_description`, `consistency_description`, `compatibility_description`, `chain_description`, `checkpoint_reason`, `created_by`.

Старый каталог без id использует name как временную идентичность. Для повторяющихся названий новый backend обязан выдавать разные стабильные id. UI сохраняет сырую запись в запросе, но это не доверенный план записи.

`history`: записи date/action/state/detail/id. `operations`: записи id/phase/detail. Отсутствующее поле отличается от переданного пустого массива. Служба должна передавать точные подтверждённые/неподтверждённые шаги в detail, не сворачивая частичную запись в обычный успех.

## Действия и Options

| Action | Options |
| --- | --- |
| create | label, description, kind: disk/ram/external, include_config: bool, consistency: crash/filesystem |
| edit | label, description |
| restore / restore_start | restore_scope: disks/disks_and_config, start_after: bool, confirm |
| delete | confirm |
| protect | protected: bool |
| export | export_scope: snapshot/current, path, format: qcow2/raw, include_config: bool, verify: bool |
| schedule | enabled, frequency: daily/weekly/interval, time: HH:mm, weekday: mon..sun, interval (дни), keep_count, keep_days, when_offline: skip/create, preserve_protected: true |
| recover | operation_id |
| cancel | operation_id |

Snapshot содержит исходную выбранную запись (для create/schedule/recover она не должна определять целевой диск). Адаптер привязан к конкретной VM. У расписания входное `schedule.interval_days` отображается в поле interval; адаптер переводит его обратно в схему хранилища. `runtime_description` показывается пользователю как описание области работы расписания.

Результат может содержать `checkpoint` — реальный ID/имя страховочной точки. Если операция прошла, но следующий Load упал, окно сообщает именно об ошибке обновления и запрещает повтор до чтения каталога.

## Обязательства backend

Перед записью повторно проверить состояние VM, идентичность дисков, общие/backing-зависимости, точные ID, хеш/актуальность плана и поддержку режима. Ввести операции preview/plan внутри адаптера или отдельный этап согласования перед Execute, если точный перечень изменений неизвестен при открытии формы. Не трактовать UI-подтверждение как обход блокировок и проверок.

Для RAM подтвердить совместимость версии QEMU, ускорителя, CPU, устройств и внешнего состояния; для Guest Agent гарантировать thaw; для восстановления обеспечить журнал и страховочную группу; для удаления/очистки проверять зависимости; для экспорта проверять новый путь, свободное место и публиковать только проверенный результат. Все эти обязанности остаются backend-задачами исходного чеклиста.

Не включать capability заранее только ради активной кнопки. Проверять новый адаптер через `SnapshotUiTests` и отдельные интеграционные сценарии, используя только временные тестовые VM.

Дополнительные capabilities: `metadata_on_create`, `adopt`. Для adopt Snapshot — обнаруженная legacy-строка, Options.confirm — внутренний name, а не пользовательский label. RPC snapshot_adopt получает name VM, tag и confirm. Старые многодисковые группы не принимаются по совпавшему тегу.

В alpha.4 основной интерфейс передаёт `snapshotJournalSupported=true`: `support_export` поддерживает добровольный раздел `snapshot_history`, ограниченный объёмом и whitelist полей. Галочка по умолчанию снята; операции снимков сами не собирают и не публикуют отчёт.

`snapshot_export_plan` принимает VM, group_id и Options; path — новый ZIP с самостоятельными дисками выбранного format и manifest. Для current выбранная строка не требуется. `snapshot_job_start/status` выполняют подтверждённый server-owned план и возвращают реальные phase/completed/total/can_cancel. `snapshot_cancel` запрашивает отмену до безопасной границы; публикация образов не прерывается. Адаптер опрашивает состояние раз в 300 мс, UI остаётся отзывчивым. Потеря ответа не означает, что worker отменён.
