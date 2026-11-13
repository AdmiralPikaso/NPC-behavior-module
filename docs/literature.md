# Литература: как программируют врагов в играх (итерация 3)

Полный конспект: https://claude.ai/artifact/VFYu8A6xsbseah6mF1zP4b

## Главное
- **Два уровня ИИ.** Orkin (F.E.A.R.): важнее любой техники — разделить решения бойца и отряда. Отряд
  раздаёт роли (слоты), боец выполняет приказ как одну из своих целей и может его перебить (бегство от гранаты).
- **Сложная тактика возникает из простой.** В F.E.A.R. всего 4 простых отрядных поведения (Get-to-Cover,
  Advance-Cover, Orderly-Advance, Search); фланги и «клещи» получаются сами из выбора укрытий за препятствиями.
- **Оценка позиций** (Killzone, Crysis TPS, UE EQS): кандидаты → фильтр → взвешенная сумма факторов
  (дистанция, линия огня, укрытие, расстояние до своих, стоимость пути). Разные тактики = разные веса.
- **Тактический путь** (Killzone): участки под огнём дороже — так получается настоящий обход.
- **Токены атаки** (DOOM 2016): стреляют только держатели токенов; число токенов — ручка сложности.
- **Читаемость** (Orkin): координация бесполезна, если игрок её не видит — подписи/реплики ролей.

## Решение для zero
`Squad` (координатор) + сменная тактика, `TacticalQuery` (оценка точек), бой на тех же действиях `NpcAction`.
Тактики: 0 — штурм (контроль), 1 — прикрытие и перебежки, 2 — обход с фланга, 3 — окружение с токенами,
4 — оборона из укрытий. Сравнение — бот-игрок на `ActorBody`, по 10+ боёв на тактику, метрики: победы,
время, потери, урон, точность, доля времени под перекрёстным огнём.

## Источники
1. Orkin J. Three States and a Plan: The A.I. of F.E.A.R. — GDC 2006.
2. Straatman R., van der Sterren W. Killzone's AI: dynamic procedural combat tactics — GDC 2005.
3. Isla D. Handling Complexity in the Halo 2 AI — GDC 2005.
4. Champandard A., Verweij T., Straatman R. Killzone 2 Multiplayer Bots — Game AI Conference 2009.
5. Mark D. Modular Tactical Influence Maps — Game AI Pro 2, гл. 30.
6. Mark D., Dill K. Improving AI Decision Modeling Through Utility Theory — GDC 2010.
7. Jack M. Tactical Position Selection: An Architecture and Query Language — Game AI Pro 360, 2019.
8. Cyber Demons: The AI of DOOM (2016) — Game Developer.
