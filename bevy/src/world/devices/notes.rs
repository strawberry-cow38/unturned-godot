//! `NoteBody`/`NoteTexts`/`NoteReader`: readable lore notes. A placement whose
//! GUID has a row in `content/note_texts.tsv` becomes a see-through look target
//! (a box padded to 0.05 m on its thin axis), outlined white, and F opens the
//! full-screen reader. F or Esc closes it. Purely local, as in the source.
use super::*;
use crate::{
    character::nameplate::OriginalFont,
    ui::godot::{ctx, window_stretch},
    world::{WorldConfig, buildings::SOURCE_PROP},
};
use bevy::window::PrimaryWindow;

/// `NoteTexts`: GUID (lower case) -> (name, lines). Loaded once.
#[derive(Resource, Default)]
pub(crate) struct Texts {
    root: Option<std::path::PathBuf>,
    pub(crate) map: HashMap<String, (String, Vec<String>)>,
}
impl Texts {
    pub(crate) fn parse(source: &str) -> HashMap<String, (String, Vec<String>)> {
        let mut map = HashMap::new();
        for line in source.lines() {
            let c: Vec<&str> = line.split('\t').collect();
            if c.len() < 2 {
                continue;
            }
            map.insert(
                c[0].to_lowercase(),
                (c[1].to_owned(), c[2..].iter().map(|s| (*s).to_owned()).collect()),
            );
        }
        map
    }
    fn ensure(&mut self, root: &std::path::Path) {
        if self.root.as_deref() == Some(root) {
            return;
        }
        self.root = Some(root.to_owned());
        self.map = std::fs::read_to_string(root.join("note_texts.tsv"))
            .map(|s| Self::parse(&s))
            .unwrap_or_default();
    }
}

/// A placed, readable note.
#[derive(Component, Clone, Debug)]
pub(crate) struct Note {
    pub(crate) name: String,
    pub(crate) lines: Vec<String>,
}
impl Note {
    /// `NoteReader.Show`: the title drops the "Note - " prefix.
    pub(crate) fn title(&self) -> String {
        self.name.replace("Note - ", "")
    }
    pub(crate) fn body(&self) -> String {
        self.lines.join("\n")
    }
}

/// The open reader, if any (`NoteReader._root.Visible`).
#[derive(Resource, Default)]
pub(crate) struct Reader {
    pub(crate) open: Option<(String, String)>,
}

pub(crate) fn attach(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut texts: ResMut<Texts>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    objects: Query<(Entity, &WorldObject), Added<WorldObject>>,
) {
    if objects.is_empty() {
        return;
    }
    texts.ensure(&config.content_root);
    for (entity, object) in &objects {
        let Some((name, lines)) = texts.map.get(&object.guid.to_lowercase()) else {
            continue;
        };
        let path = config.content_root.join(format!("objects/{}.obj", object.asset_name));
        let Ok((_, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        // A paper note is nearly flat: pad the thin axis so the look ray has
        // something to hit. Source layer bit 6 (see-through look layer).
        let size = (data.max - data.min).max(Vec3::splat(0.05));
        let center = (data.min + data.max) * 0.5;
        commands
            .entity(entity)
            .remove::<Collider>()
            .insert(Note {
                name: name.clone(),
                lines: lines.clone(),
            })
            .insert(Device(Target::Note(object.source_index)))
            .with_child((
                Name::new("NoteBody look box"),
                Collider::cuboid(size.x * 0.5, size.y * 0.5, size.z * 0.5),
                // Look-only: SOURCE_PROP without GROUP_4, which the player's
                // walking mask includes (`NoteBody`: layer bit 6, mask 0).
                CollisionGroups::new(SOURCE_PROP, Group::ALL),
                Transform::from_translation(center),
            ));
    }
}

/// F on a focused note opens it; F (`PlayerController`'s first F branch) or
/// Esc (`NoteReader._Input`, through `close_overlay`) closes it.
#[allow(clippy::too_many_arguments)]
pub(crate) fn press(
    mut ui: ResMut<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    mut reader: ResMut<Reader>,
    notes: Query<(&Device, &Note)>,
) {
    if ui.screen == Screen::Note {
        if settings.just_pressed(GameAction::Interact, &keys, &mouse) {
            ui.screen = Screen::Playing;
            reader.open = None;
        }
        return;
    }
    reader.open = None;
    for (device, note) in &notes {
        if pressed_target(&ui, &settings, &keys, &mouse, &focus, device.0) {
            reader.open = Some((note.title(), note.body()));
            ui.screen = Screen::Note;
            return;
        }
    }
}

#[derive(Component)]
pub(crate) struct ReaderRoot;

/// `NoteReader._Ready`: a 0.78 black dim, a paper panel (0.93, 0.90, 0.82)
/// with a 2 px (0.2, 0.18, 0.13) border and 26 px margins, a 12 px-separated
/// 560 px column: centred 24 px title, separator, 17 px wrapped body, 12 px
/// hint. Centred horizontally; `max(40, (h - panel) / 2)` from the top.
/// Canvas layer 91: above the map, under the console.
pub(crate) fn draw(
    mut commands: Commands,
    reader: Res<Reader>,
    font: Option<Res<OriginalFont>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    roots: Query<Entity, With<ReaderRoot>>,
    mut drawn: Local<Option<(String, String, u32)>>,
) {
    let k = window_stretch(&windows);
    let key = reader.open.clone().map(|(t, b)| (t, b, k.to_bits()));
    if *drawn == key {
        return;
    }
    *drawn = key;
    for root in &roots {
        commands.entity(root).despawn();
    }
    let Some((title, body)) = &reader.open else { return };
    let c = ctx(k, font.as_deref(), None);
    let ink = Color::srgb(0.2, 0.18, 0.13);
    // Godot Label line height: the font's ascent + descent (Open Sans:
    // (2189 + 600) / 2048 em) plus the default theme's 3 px line_spacing.
    let line = |size: f32| bevy::text::LineHeight::Px((size * 2789. / 2048. + 3.) * k);
    commands
        .spawn((
            ReaderRoot,
            Name::new("NoteReader"),
            Node {
                position_type: PositionType::Absolute,
                width: percent(100),
                height: percent(100),
                flex_direction: FlexDirection::Column,
                align_items: AlignItems::Center,
                ..default()
            },
            BackgroundColor(Color::srgba(0., 0., 0., 0.78)),
            GlobalZIndex(91),
        ))
        .with_children(|dim| {
            // Layout(): y = max(40, (h - panel) / 2) -- two growing spacers,
            // the upper one never shorter than 40.
            dim.spawn(Node {
                flex_grow: 1.,
                flex_basis: Val::Px(0.),
                min_height: c.px(40.),
                ..default()
            });
            dim.spawn((
                Node {
                    flex_direction: FlexDirection::Column,
                    flex_shrink: 0.,
                    row_gap: c.px(12.),
                    // StyleBoxFlat content margin 26 includes its 2 px border.
                    padding: UiRect::all(c.px(24.)),
                    border: UiRect::all(c.px(2.)),
                    ..default()
                },
                BackgroundColor(Color::srgb(0.93, 0.90, 0.82)),
                BorderColor::all(ink),
            ))
            .with_children(|col| {
                col.spawn((
                    c.wrapped(title.clone(), 24., Color::srgb(0.12, 0.10, 0.07), 560.),
                    line(24.),
                    TextLayout::justify(Justify::Center),
                ));
                // Godot HSeparator: 4 px tall, a 1 px StyleBoxLine through its
                // middle. Under the canvas stretch the line is k px, and Godot
                // rasterizes a sub-pixel line to nothing (oracle: invisible at 720p).
                col.spawn((
                    Node {
                        width: c.px(560.),
                        height: c.px(4.),
                        align_items: AlignItems::Center,
                        ..default()
                    },
                    children![(
                        Node {
                            width: percent(100),
                            height: Val::Px(k.floor()),
                            ..default()
                        },
                        BackgroundColor(Color::srgb(0.5, 0.5, 0.5)),
                    )],
                ));
                col.spawn((c.wrapped(body.clone(), 17., Color::srgb(0.15, 0.13, 0.10), 560.), line(17.)));
                col.spawn((
                    c.wrapped("F / Esc to close", 12., Color::srgb(0.4, 0.37, 0.3), 560.),
                    line(12.),
                    TextLayout::justify(Justify::Center),
                ));
            });
            dim.spawn(Node {
                flex_grow: 1.,
                flex_basis: Val::Px(0.),
                ..default()
            });
        });
}
