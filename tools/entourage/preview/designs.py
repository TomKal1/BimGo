import people
import trees

BUILT_INS = [
    ("tree-round", "Round tree", "tree", 7.0),
    ("tree-tall", "Columnar tree", "tree", 10.0),
    ("tree-spreading", "Spreading tree", "tree", 9.0),
    ("tree-conifer", "Conifer", "tree", 9.0),
    ("tree-palm", "Palm", "tree", 8.0),
    ("tree-gum", "Gum tree", "tree", 12.0),
    ("shrub-round", "Round shrub", "shrub", 1.2),
    ("person-man", "Man standing", "person", 1.78),
    ("person-woman", "Woman walking", "person", 1.66),
    ("person-child", "Child", "person", 1.15),
    ("person-phone", "Person on the phone", "person", 1.72),
]

DRAW = {
    "person-man": lambda: people.person_man(11),
    "person-woman": lambda: people.person_woman(12),
    "person-child": lambda: people.person_child(13),
    "person-phone": lambda: people.person_phone(14),
    "tree-round": lambda: trees.tree_round(1),
    "tree-tall": lambda: trees.tree_tall(2),
    "tree-spreading": lambda: trees.tree_spreading(3),
    "tree-conifer": lambda: trees.tree_conifer(4),
    "tree-palm": lambda: trees.tree_palm(5),
    "tree-gum": lambda: trees.tree_gum(9),
    "shrub-round": lambda: trees.shrub_round(6),
}


def draw(key):
    return DRAW[key]().to_rgba()
