---
name: spawn
description: "Спавн объектов через фабрики"
license: MIT
metadata:
  title: "Spawn"
  author: Jurok
  version: 1.0.0
  category: architecture
  kind: manual
---

## How we do Spawn here
Спавн осуществляется через фабрики. Zenject `PlaceholderFactory`/`BindFactory` в проекте НЕ используем — у нас свой шаблон:
- Контракт фабрики: `I<Thing>Factory : IMonoBehaviourFactory<<Thing>View, I<Thing>View>` (namespace `_Scripts.Infrastructure.Factories`), метод `Create(prefab, position, rotation, parent)` + поток `OnSpawn` (UniRx).
- Реализация: `[Inject] private DiContainer _di;` и внутри `Create` — `_di.InstantiatePrefabForComponent<...>(prefab, position, rotation, parent)`, чтобы у созданного объекта отработал `[Inject]`. После создания — `_onSpawn.OnNext(view)`.
- Вид (`<Thing>View : MonoBehaviour, I<Thing>View`) реализует `IFactoryObject`; данные отдаём ему методом `Bind(...)`/`Initialize(...)` сразу после `Create`.
- Установщик: `<Thing>FactoryInstaller : MonoBehaviourFactoryInstaller<I<Thing>Factory, <Thing>Factory>` — одна строка, кладём в `<Feature>/Installer/` и вешаем на нужный `SceneContext`.
- Префаб — `[SerializeField]` в спавнере, передаётся в `Create(...)`. Не `Resources.Load`, не Addressables.
- Спавнер: `<Feature>/Spawner/<Thing>Spawner : MonoBehaviour`, `[Inject] private I<Thing>Factory _factory;`, держит список созданных, сам их чистит.
- Частый спавн (создаём/убираем постоянно, например враги каждые N секунд) — через пул `PoolMono<T>` (namespace `_Scripts.Infrastructure.Utils.Pool`): конструктор с `DiContainer` (`new PoolMono<T>(prefab, container, diContainer, count, autoExpand: true)`), брать `GetFreeElement()`, возвращать `ReturnToPool(x)`, а не `Destroy`. Пул пока нигде в проекте не применяется — после внедрения проверь в Play Mode, что объекты возвращаются и переиспользуются.

## Example in this project
- Фабрика: `IRewardItemViewFactory` (namespace `_Scripts.Core.Rewards.Factory`) — `Assets/_Scripts/Core/Rewards/Factory/IRewardItemViewFactory.cs`, `Assets/_Scripts/Core/Rewards/Factory/RewardItemViewFactory.cs`
- Установщик: `Assets/_Scripts/Core/Rewards/Installer/RewardItemViewFactoryInstaller.cs`, база — `Assets/_Scripts/Infrastructure/Factories/MonoBehaviourFactoryInstaller.cs`
- Спавнер по образцу: `Assets/_Scripts/Core/Meditations/Spawner/MeditationProgramSpawner.cs`
- Пул: `Assets/_Scripts/Infrastructure/Utils/Pool/PoolMono.cs`

## Don't
- `Instantiate(...)` напрямую — объект не получит `[Inject]` (так сделано в `RewardWindow` и `AllNotesPanelHandler` — это не образец).
- `_di.InstantiatePrefabForComponent` прямо в спавнере мимо фабрики (как в `CalendarDaySpawner`) — делай фабрику.
- `BranchViewFactory` (TreeGame) — не образец: игнорирует префаб и собирает объект через `new GameObject`.
- `Destroy` для часто спавнящихся объектов — возвращай в пул.
- Статические синглтоны и `Resources.Load` для префабов.
