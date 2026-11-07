# 08. Отчёты и audit log

Агент производит два артефакта с разными целями:

| Артефакт | Для кого | Вопрос | Формат |
|---|---|---|---|
| **Отчёт** (report) | Человек | «Что случилось и что агент сделал?» | Markdown |
| **Audit log** | Человек + машина (разбор, evals) | «Какие решения, на каком основании, с каким итогом?» | JSON Lines, append-only |

## Отчёт об инциденте

Один отчёт на один запуск агента (`AgentRun`). Имя файла:
`reports/YYYY-MM-DD/HHmmss-<trigger>-<короткий-слаг>.md`.

Структура (по мотивам blameless postmortem):

```markdown
# [RESOLVED] OrdersApiHighErrorRate — orders-api

- Запуск: 2026-11-07 14:32:10Z, триггер: alertmanager webhook
- Итераций Ralph loop: 2 из 5, итог: completion check passed (error rate < 1% 5 мин)
- Длительность: 6 мин 12 с, шагов LLM: 11, вызовов tools: 14, модель: qwen2.5-14b (LM Studio)
- Режим действий: approve

## Кратко
Одна-две фразы: что сломалось, почему (с уверенностью), что сделано, текущее состояние.

## Наблюдения
- Алерт и его labels
- Ключевые метрики (до/во время) — таблица
- Состояние подов и события
- Выдержка из логов (ограниченная)

## Анализ
Гипотеза причины, на каких фактах основана, уверенность (low/medium/high),
альтернативные гипотезы, которые отброшены и почему.

## Действия
| # | Действие | Цель | Guardrails | Режим | Результат | Эффект |
|---|---|---|---|---|---|---|
| 1 | restart | deployment/orders-api | passed | approved by human | ok | improved |

## Итерации
По одному блоку на итерацию Ralph loop: гипотеза, действие, эффект, что записано в RunState.

## Таймлайн
Хронология: алерт → итерация 1 (наблюдения → анализ → действие → проверка) → итерация 2 → … → completion check.

## Рекомендации
Что агент не может/не должен делать сам: поправить limits, починить код, добавить алерт.
```

Детерминированные разделы (наблюдения, действия, таймлайн) собирает код из `AgentRun`.
Модель пишет только «Кратко», «Анализ» и «Рекомендации» — так отчёт не может «выдумать»
действия, которых не было.

## Периодический отчёт о здоровье

По таймеру (например, раз в сутки): сводка SLO и error budget, топ рестартов, алерты за период,
действия агента за период и их эффективность. Тот же формат, `trigger: schedule`.

## Audit log

Одна запись JSON на событие, файл `reports/audit/YYYY-MM-DD.jsonl` (позже — также в Loki).

```json
{"ts":"2026-11-07T14:32:51Z","runId":"7f3c...","event":"action.proposed","action":{"type":"restart","target":"deployment/orders-api","ns":"sandbox"},"reason":"OOMKilled x3 за 10 мин, память растёт линейно"}
{"ts":"2026-11-07T14:32:51Z","runId":"7f3c...","event":"guardrails.evaluated","checks":{"allowlist":"pass","policy":"pass","rateLimit":"pass"},"mode":"approve"}
{"ts":"2026-11-07T14:34:02Z","runId":"7f3c...","event":"action.approved","by":"human"}
{"ts":"2026-11-07T14:34:03Z","runId":"7f3c...","event":"action.executed","result":"ok"}
{"ts":"2026-11-07T14:36:03Z","runId":"7f3c...","event":"action.verified","effect":"improved","signals":{"errorRate":{"before":0.31,"after":0.004}}}
```

Типы событий: `run.started`, `iteration.started`, `observation.collected`, `llm.step`, `tool.called`,
`action.proposed`, `guardrails.evaluated`, `action.approved|rejected|expired`,
`action.executed`, `action.verified`, `iteration.finished`, `completion.checked`,
`run.escalated`, `run.finished`. Каждая запись несёт `iteration` — номер итерации Ralph loop.

Свойства:
- **append-only**: записи не меняются, только добавляются;
- запись `action.proposed` делается **до** выполнения — если агент упадёт посередине, след останется;
- секреты и полные промпты не пишутся; полные диалоги с LLM — отдельно, в debug-режиме.

## Хранение

- Локальный запуск агента: папка `reports/` в репозитории (в `.gitignore`, кроме примеров).
- Внутри кластера: PVC, смонтированный в под агента; плюс логи в Loki.
- Позже: простая страница/эндпоинт агента со списком последних отчётов.

## Использование для evals

Audit log + сценарии из [07-chaos-engineering.md](07-chaos-engineering.md) = данные для оценки агента:
по каждому сценарию сравнить найденную причину и действия с ожидаемыми, посчитать шаги/токены/время.
Так можно сравнивать модели (облачная vs локальная) и версии промптов.
