import { useState } from "react";
import type { MouseEvent } from "react";
import { useDirectoryListingQuery } from "../../api/queries";
import { CheckIcon, CloseIcon, FolderIcon } from "../../lib/icons";

/**
 * Ported from openDirectoryBrowser()/loadDirectory() in the original app.js. Renders a modal
 * that lets the user browse the local filesystem visible to the harness process and pick a
 * repository folder.
 */
export function DirectoryBrowserModal({
  initialPath,
  onClose,
  onSelect
}: {
  initialPath: string;
  onClose: () => void;
  onSelect: (path: string) => void;
}) {
  const [path, setPath] = useState(initialPath);
  const listingQuery = useDirectoryListingQuery(path, true);
  const listing = listingQuery.data;
  const currentPath = listing?.currentPath ?? "";

  function onBackdropClick(event: MouseEvent<HTMLDivElement>) {
    if (event.target === event.currentTarget) onClose();
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby="directory-title">
        <div className="modal-head">
          <div>
            <h3 id="directory-title">Choose source project</h3>
            <p>Browse folders visible to this local harness process.</p>
          </div>
          <button className="icon-button" aria-label="Close" onClick={onClose}>
            <CloseIcon />
          </button>
        </div>
        <div className="modal-body">
          <div className="browser-path">
            <code>{listingQuery.isLoading ? "Loading..." : currentPath || "File system"}</code>
          </div>
          <div className={`directory-list ${listingQuery.isFetching ? "loading-shimmer" : ""}`} style={{ minHeight: 260 }}>
            {listingQuery.isError ? (
              <div className="empty-state" style={{ minHeight: 220 }}>
                <p>{listingQuery.error instanceof Error ? listingQuery.error.message : "Unable to list directories."}</p>
              </div>
            ) : listing ? (
              <>
                {listing.parentPath && (
                  <button className="directory-row" onClick={() => setPath(listing.parentPath!)}>
                    <span className="folder-icon">..</span>
                    <span>Parent folder</span>
                  </button>
                )}
                {(!listing.currentPath ? listing.locations : listing.directories).map(entry => (
                  <button className="directory-row" key={entry.path} onClick={() => setPath(entry.path)}>
                    <span className="folder-icon">
                      <FolderIcon />
                    </span>
                    <span className="truncate">{entry.name}</span>
                  </button>
                ))}
                {!listing.parentPath &&
                  (!listing.currentPath ? listing.locations : listing.directories).length === 0 && (
                    <div className="empty-state" style={{ minHeight: 220 }}>
                      <p>No child folders are visible.</p>
                    </div>
                  )}
              </>
            ) : null}
          </div>
        </div>
        <div className="modal-foot">
          <button className="button" onClick={onClose}>
            Cancel
          </button>
          <button
            className="button primary"
            disabled={!currentPath}
            onClick={() => currentPath && onSelect(currentPath)}
          >
            <CheckIcon /> Select this folder
          </button>
        </div>
      </div>
    </div>
  );
}
