import * as vscode from 'vscode';
import type { StateSnapshotNotification, TestResultDto, TestSuiteDto } from '../protocol/types';

/**
 * Maps engine state snapshots to VS Code test items.
 */
export function updateTestItems(
  controller: vscode.TestController,
  snapshot: StateSnapshotNotification,
  itemMap: Map<string, vscode.TestItem>,
): void {
  const seenIds = new Set<string>();

  for (const suite of snapshot.suites) {
    const suiteItem = getOrCreateSuiteItem(controller, suite, itemMap);
    seenIds.add(suiteItem.id);

    for (const test of suite.tests) {
      const testItem = getOrCreateTestItem(controller, suiteItem, test, itemMap);
      seenIds.add(testItem.id);
    }

    // Remove stale children from this suite.
    const toRemove: string[] = [];
    suiteItem.children.forEach((child) => {
      if (!seenIds.has(child.id)) {
        toRemove.push(child.id);
      }
    });
    for (const id of toRemove) {
      suiteItem.children.delete(id);
      itemMap.delete(id);
    }
  }

  // Remove stale suite items.
  const staleRoots: string[] = [];
  controller.items.forEach((item) => {
    if (!seenIds.has(item.id)) {
      staleRoots.push(item.id);
    }
  });
  for (const id of staleRoots) {
    controller.items.delete(id);
    itemMap.delete(id);
  }
}

function getOrCreateSuiteItem(
  controller: vscode.TestController,
  suite: TestSuiteDto,
  itemMap: Map<string, vscode.TestItem>,
): vscode.TestItem {
  const id = `suite:${suite.name}`;
  let item = itemMap.get(id);
  if (!item) {
    item = controller.createTestItem(id, suite.name);
    controller.items.add(item);
    itemMap.set(id, item);
  }
  return item;
}

function getOrCreateTestItem(
  controller: vscode.TestController,
  suiteItem: vscode.TestItem,
  test: TestResultDto,
  itemMap: Map<string, vscode.TestItem>,
): vscode.TestItem {
  const id = test.fullyQualifiedName;
  let item = itemMap.get(id);

  // Compute URI from source path if available.
  let uri: vscode.Uri | undefined;
  if (test.source) {
    try {
      uri = vscode.Uri.file(test.source);
    } catch {
      // Invalid path — leave uri unset.
    }
  }

  if (!item) {
    item = controller.createTestItem(id, test.displayName, uri);
    suiteItem.children.add(item);
    itemMap.set(id, item);
  } else {
    // Update label; uri cannot change after creation.
    item.label = test.displayName;
  }

  // Set range for navigation (line 0 until we have precise line data).
  if (uri) {
    item.range = new vscode.Range(0, 0, 0, 0);
  }

  return item;
}
