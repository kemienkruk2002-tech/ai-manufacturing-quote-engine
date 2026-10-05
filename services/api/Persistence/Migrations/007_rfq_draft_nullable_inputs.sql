ALTER TABLE quote_requests
    ALTER COLUMN part_revision_id DROP NOT NULL,
    ALTER COLUMN requested_quantity DROP NOT NULL;

ALTER TABLE quote_requests
    ADD CONSTRAINT quote_requests_inputs_required_for_progressed_status
    CHECK (
        status IN ('New','DataReview','Blocked')
        OR (part_revision_id IS NOT NULL AND requested_quantity IS NOT NULL)
    );
