import pytest

@pytest.fixture(autouse=True)
def isolated_engineering_audit_database(tmp_path, monkeypatch):
    """Never write simulated test actions into the operational audit history."""
    monkeypatch.setenv('NARIT_AUDIT_DB', str(tmp_path/'engineering-test.sqlite3'))

    monkeypatch.setenv('NARIT_POINTING_WORKBENCH',str(tmp_path/'pointing-workbench.json'))

    monkeypatch.setenv('NARIT_CONFIG_DB',str(tmp_path/'observatory-test.sqlite3'))
    monkeypatch.setenv('NARIT_IMAGE_DB',str(tmp_path/'camera-images-test.sqlite3'))
